// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace ExeComp
{
    public class Statistics
    {
        private readonly ExecutionTrace[] _executionTraces;
        private readonly InstructionMap[] _instructionMaps;
        private readonly TextWriter _writer;

        public Statistics(IEnumerable<ExecutionTrace> executionTraces, TextWriter writer)
        {
            _executionTraces = executionTraces.ToArray();
            _instructionMaps = new InstructionMap[_executionTraces.Length];
            _writer = writer;
        }

        public InstructionMap GetTraceMap(int traceIndex)
        {
            InstructionMap map = _instructionMaps[traceIndex];
            if (map == null)
            {
                map = new InstructionMap();
                map.Add(_executionTraces[traceIndex]);
                _instructionMaps[traceIndex] = map;
            }
            return map;
        }

        public void DumpModuleSummary()
        {
            Dictionary<string, long[]> moduleInstructionCounts = new Dictionary<string, long[]>();
            StringBuilder headerLine = new StringBuilder();
            for (int traceIndex = 0; traceIndex < _executionTraces.Length; traceIndex++)
            {
                headerLine.AppendFormat("INSTR/{0,-4} | PERCENT/{0,-2} | ", traceIndex);
                if (traceIndex > 0)
                {
                    headerLine.AppendFormat("DELTA/{0,-4} | ", traceIndex);
                }
                foreach (ThreadExecution threadExec in _executionTraces[traceIndex].Threads.Values)
                {
                    foreach (CodeStream codeStream in threadExec.CodeStreams)
                    {
                        string module = (codeStream.ModuleSymbol.Module == "" ? "(dynamic)" : codeStream.ModuleSymbol.Module);
                        if (!moduleInstructionCounts.TryGetValue(module, out long[]? instructionCounts))
                        {
                            instructionCounts = new long[_executionTraces.Length];
                            moduleInstructionCounts.Add(module, instructionCounts);
                        }
                        instructionCounts[traceIndex] += codeStream.Instructions.Count;
                    }
                }
            }
            headerLine.Append("MODULE");

            _writer.WriteLine(headerLine.ToString());
            long[] totals = new long[_executionTraces.Length];
            for (int traceIndex = 0; traceIndex < _executionTraces.Length; traceIndex++)
            {
                totals[traceIndex] = moduleInstructionCounts.Sum(kvp => kvp.Value[traceIndex]);
                _writer.Write("{0,10} | {1,10:F1} | ", totals[traceIndex], 100);
                if (traceIndex > 0)
                {
                    _writer.Write("{0,10} | ", totals[traceIndex] - totals[0]);
                }
            }
            _writer.WriteLine("(total)");
            foreach (KeyValuePair<string, long[]> moduleCounts in moduleInstructionCounts.OrderByDescending(kvp => kvp.Value.Max()))
            {
                for (int traceIndex = 0; traceIndex < _executionTraces.Length; traceIndex++)
                {
                    _writer.Write("{0,10} | {1,10:F1} | ", moduleCounts.Value[traceIndex], moduleCounts.Value[traceIndex] * 100.0 / totals[traceIndex]);
                    if (traceIndex > 0)
                    {
                        _writer.Write("{0,10} | ", moduleCounts.Value[traceIndex] - moduleCounts.Value[0]);
                    }
                }
                _writer.WriteLine(moduleCounts.Key);
            }
            _writer.WriteLine();
        }

        public void DumpModuleInstructionMap()
        {
            InstructionMap[] traceMaps = new InstructionMap[_executionTraces.Length];
            for (int traceIndex = 0; traceIndex < _executionTraces.Length; traceIndex++)
            {
                traceMaps[traceIndex] = GetTraceMap(traceIndex);
            }
            StringBuilder moduleHeader = new StringBuilder();
            StringBuilder symbolHeader = new StringBuilder();
            moduleHeader.Append("     DELTA | ");
            symbolHeader.Append("     DELTA | ");
            Dictionary<string, InstructionSequence> moduleToSequenceDelta = new Dictionary<string, InstructionSequence>();
            // Dictionary<ModuleSymbol, InstructionSequence> symbolToSequenceDelta = new Dictionary<ModuleSymbol, InstructionSequence>();
            Dictionary<string, InstructionSequence> symbolToSequenceDelta = new Dictionary<string, InstructionSequence>();
            Dictionary<string, KeyValuePair<int, InstructionSequence>>[] perTraceSymbolInstructionSequenceMap
                = new Dictionary<string, KeyValuePair<int, InstructionSequence>>[_executionTraces.Length];
            for (int i = 0; i < _executionTraces.Length; i++)
            {
                InstructionMap traceMap = traceMaps[i];
                moduleHeader.AppendFormat("INSTR/{0,-4} | CALLS/{0,-4} | ", i);
                symbolHeader.AppendFormat("INSTR/{0,-4} | CALLS/{0,-4} | AVGIC/{0,-4} | ", i);
                Dictionary<string, KeyValuePair<int, InstructionSequence>> traceSymbolInstructionMap = new Dictionary<string, KeyValuePair<int, InstructionSequence>>();
                perTraceSymbolInstructionSequenceMap[i] = traceSymbolInstructionMap;
                foreach (KeyValuePair<string, ModuleInstructionMap> kvpModuleMap in traceMap.Map)
                {
                    moduleToSequenceDelta.TryGetValue(kvpModuleMap.Key, out InstructionSequence moduleInstructions);
                    switch (i)
                    {
                        case 0:
                            moduleToSequenceDelta[kvpModuleMap.Key] = kvpModuleMap.Value.ModuleInstructions;
                            break;

                        case 1:
                            moduleToSequenceDelta[kvpModuleMap.Key] = moduleInstructions.Minus(kvpModuleMap.Value.ModuleInstructions);
                            break;
                    }
                    foreach (KeyValuePair<string, InstructionSequence> kvpSymbolInstructions in kvpModuleMap.Value.SymbolExclusiveMap)
                    {
                        // ModuleSymbol moduleSymbol = new ModuleSymbol(kvpModuleMap.Key, kvpSymbolInstructions.Key);
                        string symbol = kvpSymbolInstructions.Key;
                        // kvpModuleMap.Value.SymbolCallCountMap.TryGetValue(symbol, out int callCount);
                        kvpModuleMap.Value.SymbolCallerMap.TryGetValue(symbol, out Dictionary<string, int>? callerCount);
                        int callCount = callerCount?.Values.Sum() ?? 0;
                        traceSymbolInstructionMap.TryGetValue(symbol, out KeyValuePair<int, InstructionSequence> callCountAndInstructionSequence);
                        traceSymbolInstructionMap[symbol] = new KeyValuePair<int, InstructionSequence>(
                            callCountAndInstructionSequence.Key + callCount,
                            callCountAndInstructionSequence.Value.Plus(kvpSymbolInstructions.Value));
                        switch (i)
                        {
                            case 0:
                                symbolToSequenceDelta[symbol] = kvpSymbolInstructions.Value;
                                break;

                            case 1:
                                {
                                    symbolToSequenceDelta.TryGetValue(symbol, out InstructionSequence firstTraceInstructions);
                                    symbolToSequenceDelta[symbol] = firstTraceInstructions.Minus(kvpSymbolInstructions.Value);
                                }
                                break;
                        }
                    }
                }

            }
            moduleHeader.AppendFormat("MODULE");
            symbolHeader.AppendFormat("SYMBOL");
            _writer.WriteLine(moduleHeader.ToString());
            _writer.WriteLine(new string('-', moduleHeader.Length));
            _writer.Write("{0,10} | ", moduleToSequenceDelta.Sum(v => v.Value.Count));
            for (int i = 0; i < _executionTraces.Length; i++)
            {
                InstructionMap traceMap = traceMaps[i];
                long instr = traceMap.Map.Sum(m => m.Value.ModuleInstructions.Count);
                // long calls = traceMap.Map.Sum(m => m.Value.SymbolCallCountMap.Values.Sum());
                long calls = traceMap.Map.Sum(m => m.Value.SymbolCallerMap.Values.Sum(c => c.Values.Sum()));
                _writer.Write("{0,10} | {1,10} | ", instr, calls);
            }
            _writer.WriteLine("(total)");
            foreach (KeyValuePair<string, InstructionSequence> kvpModuleInstructions in moduleToSequenceDelta.OrderByDescending(m => m.Value.Count))
            {
                _writer.Write("{0,10} | ", kvpModuleInstructions.Value.Count);
                for (int i = 0; i < _executionTraces.Length; i++)
                {
                    if (traceMaps[i].Map.TryGetValue(kvpModuleInstructions.Key, out ModuleInstructionMap? moduleMap))
                    {
                        //int totalCallCount = moduleMap.SymbolCallCountMap.Values.Sum();
                        int totalCallCount = moduleMap.SymbolCallerMap.Values.Sum(c => c.Values.Sum());
                        _writer.Write("{0,10} | {1,10} | ", moduleMap.ModuleInstructions.Count, totalCallCount);
                    }
                    else
                    {
                        _writer.Write("       --- |        --- | ");
                    }
                }
                _writer.WriteLine(kvpModuleInstructions.Key);
            }
            _writer.WriteLine();

            _writer.WriteLine(symbolHeader.ToString());
            _writer.WriteLine(new string('-', symbolHeader.Length));
            foreach (KeyValuePair<string, InstructionSequence> kvpSymbolInstructions in symbolToSequenceDelta.Where(m => m.Value.Count != 0).OrderByDescending(m => m.Value.Count))
            {
                _writer.Write("{0,10} | ", kvpSymbolInstructions.Value.Count);
                for (int i = 0; i < _executionTraces.Length; i++)
                {
                    if (perTraceSymbolInstructionSequenceMap[i].TryGetValue(kvpSymbolInstructions.Key, out KeyValuePair<int, InstructionSequence> callCountAndInstructions))
                    {
                        double averageInstructionCount = callCountAndInstructions.Value.Count / (double)Math.Max(callCountAndInstructions.Key, 1);
                        _writer.Write("{0,10} | {1,10} | {2,10:F0} | ", callCountAndInstructions.Value.Count, callCountAndInstructions.Key, averageInstructionCount);
                    }
                    else
                    {
                        _writer.Write("       --- |        --- |        --- | ");
                    }
                }
                _writer.WriteLine(kvpSymbolInstructions.Key);
            }
            _writer.WriteLine();
        }

        public void DumpModuleInclusiveCounts()
        {
            InstructionMap map = GetTraceMap(0);
            foreach (KeyValuePair<string, ModuleInstructionMap> moduleMapKvp in map.Map.OrderByDescending(m => m.Value.ModuleInstructions.Count).Take(10))
            {
                string title = $"INCL.COUNT | METHOD NAME (in {moduleMapKvp.Key})";
                _writer.WriteLine(title);
                _writer.WriteLine(new string('-', title.Length));
                foreach (KeyValuePair<string, InstructionSequence> symbolInclusive in moduleMapKvp.Value.SymbolInclusiveMap.OrderByDescending(m => m.Value.Count).Take(100))
                {
                    _writer.WriteLine("{0,10} | {1}", symbolInclusive.Value.Count, symbolInclusive.Key);
                }
                _writer.WriteLine();
            }
        }

        public void DumpReverseCallGraph(string calleeSymbol)
        {
            InstructionMap map = GetTraceMap(0);
            Dictionary<string, int> symbolLevelMap = new Dictionary<string, int>();
            int level = 0;
            symbolLevelMap.Add(calleeSymbol, level);
            bool makingProgress;
            do
            {
                ++level;
                makingProgress = false;
                HashSet<string> nextCallerLevel = new HashSet<string>();
                foreach (KeyValuePair<string, int> symbolLevelKvp in symbolLevelMap)
                {
                    foreach (ModuleInstructionMap moduleMap in map.Map.Values)
                    {
                        if (moduleMap.SymbolCallerMap.TryGetValue(symbolLevelKvp.Key, out Dictionary<string, int>? callerCounts))
                        {
                            foreach (string caller in callerCounts.Keys)
                            {
                                if (!symbolLevelMap.ContainsKey(caller))
                                {
                                    nextCallerLevel.Add(caller);
                                    makingProgress = true;
                                }
                            }
                            break;
                        }
                    }
                }
                foreach (string caller in nextCallerLevel)
                {
                    symbolLevelMap.Add(caller, level);
                }
            }
            while (makingProgress && symbolLevelMap.Count < 1000);

            _writer.WriteLine("LEVEL | REVERSE CALL GRAPH");
            _writer.WriteLine("--------------------------");

            foreach (KeyValuePair<string, int> callerLevel in symbolLevelMap.OrderBy(kvp => kvp, CallerLevelComparer.Instance))
            {
                _writer.WriteLine("{0,5} | {1}", callerLevel.Value, callerLevel.Key);
            }

            _writer.WriteLine();
        }

        private sealed class CallerLevelComparer : IComparer<KeyValuePair<string, int>>
        {
            public static CallerLevelComparer Instance = new CallerLevelComparer();

            public int Compare(KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                int result = x.Value.CompareTo(y.Value);
                if (result == 0)
                {
                    result = StringComparer.InvariantCulture.Compare(x.Key, y.Key);
                }
                return result;
            }
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace ExeComp
{
    public class Statistics
    {
        private readonly ExecutionTrace[] _executionTraces;
        private readonly TextWriter _writer;

        public Statistics(IEnumerable<ExecutionTrace> executionTraces, TextWriter writer)
        {
            _executionTraces = executionTraces.ToArray();
            _writer = writer;
        }

        public void DumpModuleInstructionMap()
        {
            InstructionMap[] traceMaps = new InstructionMap[_executionTraces.Length];
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
                InstructionMap traceMap = new InstructionMap();
                traceMap.Add(_executionTraces[i]);
                traceMaps[i] = traceMap;
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
                            moduleToSequenceDelta[kvpModuleMap.Key] = kvpModuleMap.Value.ModuleInstructions.Negated();
                            break;

                        case 1:
                            moduleToSequenceDelta[kvpModuleMap.Key] = kvpModuleMap.Value.ModuleInstructions.Plus(moduleInstructions);
                            break;
                    }

                    foreach (KeyValuePair<string, InstructionSequence> kvpSymbolInstructions in kvpModuleMap.Value.SymbolInstructionMap)
                    {
                        // ModuleSymbol moduleSymbol = new ModuleSymbol(kvpModuleMap.Key, kvpSymbolInstructions.Key);
                        string symbol = kvpSymbolInstructions.Key;
                        kvpModuleMap.Value.SymbolCallCountMap.TryGetValue(symbol, out int callCount);
                        traceSymbolInstructionMap.TryGetValue(symbol, out KeyValuePair<int, InstructionSequence> callCountAndInstructionSequence);
                        traceSymbolInstructionMap[symbol] = new KeyValuePair<int, InstructionSequence>(
                            callCountAndInstructionSequence.Key + callCount,
                            callCountAndInstructionSequence.Value.Plus(kvpSymbolInstructions.Value));
                        switch (i)
                        {
                            case 0:
                                {
                                    symbolToSequenceDelta.TryGetValue(symbol, out InstructionSequence instructions);
                                    symbolToSequenceDelta[symbol] = instructions.Minus(kvpSymbolInstructions.Value);
                                }
                                break;

                            case 1:
                                {
                                    symbolToSequenceDelta.TryGetValue(symbol, out InstructionSequence instructions);
                                    symbolToSequenceDelta[symbol] = instructions.Plus(kvpSymbolInstructions.Value);
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
                long calls = traceMap.Map.Sum(m => m.Value.SymbolCallCountMap.Values.Sum());
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
                        int totalCallCount = moduleMap.SymbolCallCountMap.Values.Sum();
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
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;

class Statistics
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
        Dictionary<ModuleSymbol, InstructionSequence> symbolToSequenceDelta = new Dictionary<ModuleSymbol, InstructionSequence>();
        for (int i = 0; i < _executionTraces.Length; i++)
        {
            InstructionMap traceMap = new InstructionMap();
            traceMap.Add(_executionTraces[i]);
            traceMaps[i] = traceMap;
            moduleHeader.AppendFormat("INSTR/{0,-4} | ", i);
            symbolHeader.AppendFormat("INSTR/{0,-4} | ", i);
            foreach (KeyValuePair<string, ModuleInstructionMap> kvpModuleMap in traceMap.Map)
            {
                moduleToSequenceDelta.TryGetValue(kvpModuleMap.Key, out InstructionSequence moduleInstructions);
                switch (i)
                {
                    case 0:
                        moduleToSequenceDelta[kvpModuleMap.Key] = kvpModuleMap.Value.ModuleInstructions;
                        break;

                    case 1:
                        moduleToSequenceDelta[kvpModuleMap.Key] = kvpModuleMap.Value.ModuleInstructions.Minus(moduleInstructions);
                        break;
                }
                foreach (KeyValuePair<string, InstructionSequence> kvpSymbolInstructions in kvpModuleMap.Value.SymbolInstructionMap)
                {
                    ModuleSymbol moduleSymbol = new ModuleSymbol(kvpModuleMap.Key, kvpSymbolInstructions.Key);
                    symbolToSequenceDelta.TryGetValue(moduleSymbol, out InstructionSequence symbolInstructions);
                    switch (i)
                    {
                        case 0:
                            symbolToSequenceDelta[moduleSymbol] = kvpSymbolInstructions.Value;
                            break;

                        case 1:
                            symbolToSequenceDelta[moduleSymbol] = kvpSymbolInstructions.Value.Minus(symbolInstructions);
                            break;
                    }
                }
            }

        }
        moduleHeader.AppendFormat("MODULE NAME");
        symbolHeader.AppendFormat("MODULE / SYMBOL NAME");
        _writer.WriteLine(moduleHeader.ToString());
        _writer.WriteLine(new String('-', moduleHeader.Length));
        foreach (KeyValuePair<string, InstructionSequence> kvpModuleInstructions in moduleToSequenceDelta.Where(m => m.Value.Count != 0).OrderByDescending(m => m.Value.Count))
        {
            _writer.Write("{0,10} | ", kvpModuleInstructions.Value.Count);
            for (int i = 0; i < _executionTraces.Length; i++)
            {
                if (traceMaps[i].Map.TryGetValue(kvpModuleInstructions.Key, out ModuleInstructionMap? moduleMap))
                {
                    _writer.Write("{0,10} | ", moduleMap.ModuleInstructions.Count);
                }
                else
                {
                    _writer.Write("<UNUSED>   | ");
                }
            }
            _writer.WriteLine(kvpModuleInstructions.Key);
        }
        _writer.WriteLine();

        _writer.WriteLine(symbolHeader.ToString());
        _writer.WriteLine(new String('-', symbolHeader.Length));
        foreach (KeyValuePair<ModuleSymbol, InstructionSequence> kvpSymbolInstructions in symbolToSequenceDelta.Where(m => m.Value.Count != 0).OrderByDescending(m => m.Value.Count))
        {
            _writer.Write("{0,10} | ", kvpSymbolInstructions.Value.Count);
            for (int i = 0; i < _executionTraces.Length; i++)
            {
                if (traceMaps[i].Map.TryGetValue(kvpSymbolInstructions.Key.Module, out ModuleInstructionMap? moduleMap)
                    && moduleMap.SymbolInstructionMap.TryGetValue(kvpSymbolInstructions.Key.Symbol, out InstructionSequence symbolInstructions))
                {
                    _writer.Write("{0,10} | ", symbolInstructions.Count);
                }
                else
                {
                    _writer.Write("<UNUSED>   | ");
                }
            }
            _writer.WriteLine(kvpSymbolInstructions.Key.Symbol);
        }
        _writer.WriteLine();
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

class ModuleInstructionMap
{
    public InstructionSequence ModuleInstructions;
    public readonly Dictionary<string, InstructionSequence> SymbolInstructionMap = new Dictionary<string, InstructionSequence>();
}


class InstructionMap
{
    public readonly Dictionary<string, ModuleInstructionMap> Map = new Dictionary<string, ModuleInstructionMap>();

    public void Add(ThreadExecution threadExec)
    {
        foreach (CodeStream codeStream in threadExec.CodeStreams)
        {
            if (!Map.TryGetValue(codeStream.ModuleSymbol.Module, out ModuleInstructionMap? moduleMap))
            {
                moduleMap = new ModuleInstructionMap();
                Map.Add(codeStream.ModuleSymbol.Module, moduleMap);
            }
            moduleMap.ModuleInstructions = moduleMap.ModuleInstructions.Plus(codeStream.Instructions);
            moduleMap.SymbolInstructionMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out InstructionSequence symbolInstructions);
            moduleMap.SymbolInstructionMap[codeStream.ModuleSymbol.Symbol] = symbolInstructions.Plus(codeStream.Instructions);
        }
    }

    public void Add(ExecutionTrace trace)
    {
        foreach (ThreadExecution threadExec in trace.Threads.Values)
        {
            Add(threadExec);
        }
    }
}

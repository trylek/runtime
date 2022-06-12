// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

class ModuleInstructionMap
{
    public InstructionSequence ModuleInstructions;
    public readonly Dictionary<string, InstructionSequence> SymbolInstructionMap = new Dictionary<string, InstructionSequence>();
    public readonly Dictionary<string, int> SymbolCallCountMap = new Dictionary<string, int>();
}


class InstructionMap
{
    public readonly Dictionary<string, ModuleInstructionMap> Map = new Dictionary<string, ModuleInstructionMap>();


    public void Add(ThreadExecution threadExec)
    {
        HashSet<ModuleSymbol> activeFunctions = new HashSet<ModuleSymbol>();
        List<FunctionFrame> activeFrames = new List<FunctionFrame>();

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
            while (activeFrames.Count != 0 && activeFrames[activeFrames.Count - 1].SP < codeStream.SP)
            {
                activeFunctions.Remove(activeFrames[activeFrames.Count - 1].Function);
                activeFrames.RemoveAt(activeFrames.Count - 1);
            }
            if (!activeFunctions.Contains(codeStream.ModuleSymbol))
            {
                activeFunctions.Add(codeStream.ModuleSymbol);
                activeFrames.Add(new FunctionFrame(codeStream.SP, codeStream.ModuleSymbol));
                moduleMap.SymbolCallCountMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out int callCount);
                moduleMap.SymbolCallCountMap[codeStream.ModuleSymbol.Symbol] = callCount + 1;
            }
        }
    }

    public void Add(ExecutionTrace trace)
    {
        foreach (ThreadExecution threadExec in trace.Threads.Values)
        {
            Add(threadExec);
        }
    }

    private struct FunctionFrame
    {
        public readonly ulong SP;
        public readonly ModuleSymbol Function;

        public FunctionFrame(ulong sp, ModuleSymbol function)
        {
            SP = sp;
            Function = function;
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ExeComp
{
    public class ModuleInstructionMap
    {
        public InstructionSequence ModuleInstructions;
        public readonly Dictionary<string, InstructionSequence> SymbolExclusiveMap = new Dictionary<string, InstructionSequence>();
        public readonly Dictionary<string, InstructionSequence> SymbolInclusiveMap = new Dictionary<string, InstructionSequence>();
        // public readonly Dictionary<string, int> SymbolCallCountMap = new Dictionary<string, int>();
        public readonly Dictionary<string, Dictionary<string, int>> SymbolCallerMap = new Dictionary<string, Dictionary<string, int>>();
    }


    public class InstructionMap
    {
        public readonly Dictionary<string, ModuleInstructionMap> Map = new Dictionary<string, ModuleInstructionMap>();


        public void Add(ThreadExecution threadExec)
        {
            HashSet<ModuleSymbol> activeFunctions = new HashSet<ModuleSymbol>();
            List<FunctionFrame> activeFrames = new List<FunctionFrame>();

            int codeStreamIndex = 0;
            foreach (CodeStream codeStream in threadExec.CodeStreams)
            {
                if (++codeStreamIndex % 1000000 == 0)
                {
                    Console.WriteLine("Processing code stream {0} / {1}", codeStreamIndex, threadExec.CodeStreams.Count);
                }
                if (!Map.TryGetValue(codeStream.ModuleSymbol.Module, out ModuleInstructionMap? moduleMap))
                {
                    moduleMap = new ModuleInstructionMap();
                    Map.Add(codeStream.ModuleSymbol.Module, moduleMap);
                }
                moduleMap.ModuleInstructions = moduleMap.ModuleInstructions.Plus(codeStream.Instructions);
                moduleMap.SymbolExclusiveMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out InstructionSequence symbolExclusiveInstructions);
                moduleMap.SymbolExclusiveMap[codeStream.ModuleSymbol.Symbol] = symbolExclusiveInstructions.Plus(codeStream.Instructions);
                moduleMap.SymbolInclusiveMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out InstructionSequence symbolInclusiveInstructions);
                moduleMap.SymbolInclusiveMap[codeStream.ModuleSymbol.Symbol] = symbolInclusiveInstructions.Plus(codeStream.Instructions);

                while (activeFrames.Count != 0 && activeFrames[activeFrames.Count - 1].SP < codeStream.SP)
                {
                    activeFunctions.Remove(activeFrames[activeFrames.Count - 1].Function);
                    activeFrames.RemoveAt(activeFrames.Count - 1);
                }
                if (!activeFunctions.Contains(codeStream.ModuleSymbol) && codeStream.SymbolOffset == 0)
                {
                    if (activeFrames.Count > 0)
                    {
                        ModuleSymbol caller = activeFrames[activeFrames.Count - 1].Function;
                        if (!moduleMap.SymbolCallerMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out Dictionary<string, int>? callerCounts))
                        {
                            callerCounts = new Dictionary<string, int>();
                            moduleMap.SymbolCallerMap.Add(codeStream.ModuleSymbol.Symbol, callerCounts);
                        }
                        callerCounts.TryGetValue(caller.Symbol, out int callCount);
                        callerCounts[caller.Symbol] = callCount + 1;
                    }

                    activeFunctions.Add(codeStream.ModuleSymbol);
                    activeFrames.Add(new FunctionFrame(codeStream.SP, codeStream.ModuleSymbol));
                    // moduleMap.SymbolCallCountMap.TryGetValue(codeStream.ModuleSymbol.Symbol, out int callCount);
                    // moduleMap.SymbolCallCountMap[codeStream.ModuleSymbol.Symbol] = callCount + 1;
                }
                for (int frameIndex = activeFrames.Count - 2; frameIndex >= 0; frameIndex--)
                {
                    ModuleSymbol function = activeFrames[frameIndex].Function;
                    ModuleInstructionMap parentMap = Map[function.Module];
                    parentMap.SymbolInclusiveMap.TryGetValue(function.Symbol, out InstructionSequence parentInclusiveInstructions);
                    parentMap.SymbolInclusiveMap[function.Symbol] = parentInclusiveInstructions.Plus(codeStream.Instructions);
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
}

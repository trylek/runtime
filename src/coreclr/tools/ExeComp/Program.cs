// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;

namespace ExeComp
{
    public sealed class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                TryMain(args);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: {0}", ex.Message);
                return 1;
            }
        }

        private static void TryMain(string[] args)
        {
            List<ExecutionTrace> traces = new List<ExecutionTrace>();
            string? outputFile = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("-out:"))
                {
                    outputFile = arg.Substring(5);
                }
                else
                {
                    traces.Add(ExecutionTrace.LoadFile(arg));
                }
            }

            if (outputFile == null)
            {
                DumpStatistics(traces, Console.Out);
            }
            else
            {
                Console.WriteLine("Emitting statistics file: {0}", outputFile);
                using (StreamWriter writer = new StreamWriter(outputFile))
                {
                    DumpStatistics(traces, writer);
                }
            }
        }

        private static void DumpStatistics(IEnumerable<ExecutionTrace> traces, TextWriter writer)
        {
            int traceIndex = 0;
            foreach (ExecutionTrace trace in traces)
            {
                writer.WriteLine("Trace file #{0}: {1}", traceIndex, trace.FileName);
                traceIndex++;
            }

            Statistics statistics = new Statistics(traces, writer);
            statistics.DumpModuleSummary();
            statistics.DumpModuleInstructionMap();
            statistics.DumpModuleInclusiveCounts();
            statistics.DumpReverseCallGraph("icu.dll!u_strToLower");
        }
    }
}

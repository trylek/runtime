// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace JitDiff
{
    public sealed class Program
    {
        public static void Main(string[] args)
        {
            string jitListA = args[0];
            string jitListB = args[1];

            HashSet<string> jitSetA = LoadJitSet(jitListA);
            HashSet<string> jitSetB = LoadJitSet(jitListB);

            DumpTable("Methods requiring runtime JIT in both R2R and full composite mode", jitSetA.Intersect(jitSetB));
            DumpTable("Methods requiring runtime JIT only in non-composite R2R mode", jitSetA.Except(jitSetB));
            DumpTable("Methods requiring runtime JIT only in composite mode", jitSetB.Except(jitSetA));
        }

        private static void DumpTable(string title, IEnumerable<string> list)
        {
            Console.WriteLine(title + $" ({list.Count()} total)");
            Console.WriteLine(new string('-', title.Length));
            foreach (string item in list.OrderBy(s => s))
            {
                Console.WriteLine(item);
            }
            Console.WriteLine();
        }

        private static HashSet<string> LoadJitSet(string fileName)
        {
            HashSet<string> results = new HashSet<string>();
            foreach (string line in File.ReadAllLines(fileName))
            {
                int scanIndex = 0;
                while (scanIndex < line.Length && line[scanIndex] == ' ')
                {
                    scanIndex++;
                }
                int startIndex = scanIndex;
                while (scanIndex < line.Length && char.IsDigit(line[scanIndex]))
                {
                    scanIndex++;
                }
                const string compiledTag = ": JIT compiled ";
                if (scanIndex + compiledTag.Length <= line.Length && line.AsSpan(scanIndex, compiledTag.Length).Equals(compiledTag, StringComparison.Ordinal))
                {
                    startIndex = scanIndex + compiledTag.Length;
                }
                int endIndex = line.IndexOf(") [", startIndex) + 1;
                if (endIndex <= 0)
                {
                    endIndex = line.Length;
                }
                if (startIndex < endIndex)
                {
                    results.Add(line.Substring(startIndex, endIndex - startIndex));
                }
            }
            return results;
        }
    }
}

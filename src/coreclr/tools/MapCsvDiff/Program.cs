// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MapCsvDiff
{
    internal sealed class Program
    {
        public static void Main(string[] args)
        {
            List<Map> maps = new List<Map>();
            foreach (string arg in args)
            {
                maps.Add(Map.Load(arg));
            }
            Diff diff = new Diff(maps);
            diff.DumpDiff(Console.Out);
        }
    }
}

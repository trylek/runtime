// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;

class Program
{
    public static int Main(string[] args)
    {
        try
        {
            new Program().TryMain(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: {0}", ex.Message);
            return 1;
        }
    }

    private void TryMain(string[] args)
    {
        List<ExecutionTrace> traces = new List<ExecutionTrace>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            traces.Add(ExecutionTrace.LoadFile(arg));
        }
    }
}

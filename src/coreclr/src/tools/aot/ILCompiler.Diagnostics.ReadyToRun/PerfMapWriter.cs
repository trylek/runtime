// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;

namespace ILCompiler.Diagnostics
{
    public class PerfMapWriter
    {
        private TextWriter _writer;

        PerfMapWriter(TextWriter writer)
        {
            _writer = writer;
        }

        public static void Write(string path, IEnumerable<MethodInfo> methods)
        {
            using (TextWriter writer = new StreamWriter(path))
            {
                PerfMapWriter perfMapWriter = new PerfMapWriter(writer);
                foreach (MethodInfo methodInfo in methods)
                {
                    perfMapWriter.Write(methodInfo.HotRVA, methodInfo.HotLength, methodInfo.Name);
                    if (methodInfo.ColdLength != 0)
                    {
                        perfMapWriter.Write(methodInfo.ColdRVA, methodInfo.ColdLength, methodInfo.Name);
                    }
                }
            }
        }

        void Write(uint codeAddress, uint length, string functionName)
        {
            _writer.WriteLine($"{codeAddress:X8} {length:X2} {functionName}");
        }
    }
}

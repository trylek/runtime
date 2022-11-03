// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace runcg2
{
    public sealed class Program
    {
        private int _failureCount;
        private int _successCount;

        private string _folder = "";
        private string _app = "";
        private string _cg2Folder = "";
        private string _commonArguments = "";

        public static int Main(string[] args)
        {
            return new Program().TryMain(args);
        }

        private int TryMain(string[] args)
        {
            _folder = args[0];
            _app = args[1];
            StringBuilder arguments = new StringBuilder();
            for (int argIndex = 2; argIndex < args.Length; argIndex++)
            {
                string arg = args[argIndex];
                if (arguments.Length > 0)
                {
                    arguments.Append(' ');
                }
                if (arg.Contains(' ') || arg.Contains('\"'))
                {
                    arguments.Append('\"');
                    arguments.Append(arg.Replace("\"", "\"\""));
                    arguments.Append('\"');
                }
                else
                {
                    arguments.Append(arg);
                }
            }

            _commonArguments = arguments.ToString();
            _cg2Folder = Path.Combine(_folder, "CG2");
            Directory.CreateDirectory(_cg2Folder);
            string[] files = Directory.GetFiles(_cg2Folder);
            foreach (string file in files)
            {
                File.Delete(file);
            }

            List<KeyValuePair<string, long>> dllSizes = new List<KeyValuePair<string, long>>();
            foreach (string dll in Directory.EnumerateFiles(_folder, "*.dll"))
            {
                dllSizes.Add(new KeyValuePair<string, long>(dll, new FileInfo(dll).Length));
            }

            Parallel.ForEach(dllSizes.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key), CompileFile);

            Console.WriteLine("Succeeded: {0}, failed: {1}", _successCount, _failureCount);

            string[] compiledFiles = Directory.GetFiles(_cg2Folder);
            foreach (string file in compiledFiles)
            {
                File.Move(file, Path.Combine(_folder, Path.GetFileName(file)), overwrite: true);
            }

            return _failureCount == 0 ? 0 : 1;
        }

        private void CompileFile(string dll)
        {
            string output = Path.Combine(_cg2Folder, Path.GetFileName(dll));
            string fileArgs = _commonArguments;
            fileArgs += " -o:" + output;
            fileArgs += " -O";
            fileArgs += " " + dll;
            fileArgs += $" -r:{_folder}\\*.dll";
            fileArgs += " --pdb";

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = _app,
                Arguments = fileArgs,
            };

            using (Process cg2Process = Process.Start(psi)!)
            {
                cg2Process.WaitForExit();
                if (cg2Process.ExitCode != 0)
                {
                    Console.Error.WriteLine("Error compiling '{0}'", dll);
                    Interlocked.Increment(ref _failureCount);
                }
                else
                {
                    Interlocked.Increment(ref _successCount);
                }
            }

        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace runcg2
{
    public sealed class Program
    {
        private int _failureCount;
        private int _successCount;

        private string _folder = "";
        private string _appFolder = "";
        private string _app = "";
        private string _commonArguments = "";
        private HashSet<string>? _compositeAssemblies;
        private int _compositeAssemblyCount;

        public static int Main(string[] args)
        {
            return new Program().TryMain(args);
        }

        private int TryMain(string[] args)
        {
            _folder = args[0];
            int.TryParse(args[2], out _compositeAssemblyCount);
            _compositeAssemblies = LoadCompositeAssemblies(args[1], _compositeAssemblyCount);
            _app = args[3];
            StringBuilder arguments = new StringBuilder();
            for (int argIndex = 4; argIndex < args.Length; argIndex++)
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
            _appFolder = Path.Combine(_folder, "app");

            int totalFiles = 0;
            List<KeyValuePair<string, long>> dllSizes = new List<KeyValuePair<string, long>>();
            HashSet<string> compositeFiles = new HashSet<string>();
            foreach (string dll in Directory.EnumerateFiles(_folder, "*.dll"))
            {
                if (IsManagedAssembly(dll))
                {
                    totalFiles++;
                    string simpleName = Path.GetFileNameWithoutExtension(dll);
                    if (_compositeAssemblies == null || _compositeAssemblies.Contains(simpleName))
                    {
                        compositeFiles.Add(dll);
                    }
                    else
                    {
                        dllSizes.Add(new KeyValuePair<string, long>(dll, new FileInfo(dll).Length));
                    }
                }
            }

            List<string> singleFiles = new List<string>();
            foreach (string dll in dllSizes.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key))
            {
                if (compositeFiles.Count < _compositeAssemblyCount)
                {
                    compositeFiles.Add(dll);
                }
                else
                {
                    singleFiles.Add(dll);
                }
            }

            long compositeSize = 0;
            if (compositeFiles.Count > 0)
            {
                compositeSize = CompileComposite(compositeFiles);
            }

            // Parallel.ForEach(singleFiles, CompileFile);
            // Console.WriteLine("Succeeded: {0}, failed: {1}", _successCount, _failureCount);

            long singleSize = singleFiles.Sum(dll => new FileInfo(Path.Combine(_appFolder, Path.GetFileName(dll))).Length);
            long totalSize = compositeSize + singleSize;

            Console.WriteLine("### Total dlls: {0} ###", totalFiles);
            Console.WriteLine("### Composite:  {0} ###", compositeFiles.Count);
            Console.WriteLine("### CompSize:   {0} ###", compositeSize);
            Console.WriteLine("### Singlefile: {0} ###", singleFiles.Count);
            Console.WriteLine("### SingleSize: {0} ###", singleSize);
            Console.WriteLine("### R2R-length: {0} ###", totalSize);

            return _failureCount == 0 ? 0 : 1;
        }

        private static HashSet<string>? LoadCompositeAssemblies(string path, int count)
        {
            if (path == "*")
            {
                return null;
            }
            HashSet<string>? result = new HashSet<string>();
            if (!string.IsNullOrEmpty(path))
            {
                result.UnionWith(File.ReadAllLines(path).Take(count));
            }
            return result;
        }

        private void CompileFile(string dll)
        {
            string output = Path.Combine(_appFolder, Path.GetFileName(dll));
            string fileArgs = _commonArguments;
            fileArgs += " -o:" + output;
            fileArgs += " -O";
            fileArgs += " " + dll;
            fileArgs += $" -r:{_folder}\\*.dll";
            // fileArgs += " --pdb";

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

        private static bool IsManagedAssembly(string file)
        {
            using (FileStream peStream = new FileStream(file, FileMode.Open, FileAccess.Read))
            {
                using (PEReader peReader = new PEReader(peStream))
                {
                    return peReader.PEHeaders.CorHeader != null;
                }
            }
        }

        private long CompileComposite(IEnumerable<string> files)
        {
            string compositeName = "composite." + Path.GetFileNameWithoutExtension(files.First()) + ".dll";
            string output = Path.Combine(_appFolder, compositeName);
            string responseFile = output + ".rsp";
            string fileArgs = _commonArguments + " @" + responseFile;
            StringBuilder responseFileContent = new StringBuilder();
            responseFileContent.AppendLine("--composite");
            responseFileContent.AppendLine("-o:" + output);
            responseFileContent.AppendLine("-O");
            foreach (string dll in files)
            {
                responseFileContent.AppendLine(dll);
            }
            responseFileContent.AppendLine($"-r:{_folder}\\*.dll");

            Console.WriteLine("Compiling composite image {0}", output);
            Console.WriteLine("Crossgen2 args: {0}", fileArgs);
            Console.WriteLine(responseFileContent.ToString());
            File.WriteAllText(responseFile, responseFileContent.ToString());

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
                    Console.Error.WriteLine("Error compiling composite image '{0}'", output);
                    Interlocked.Increment(ref _failureCount);
                }
                else
                {
                    Interlocked.Increment(ref _successCount);
                }
            }

            long totalSize = new FileInfo(output).Length;
            foreach (string dll in files)
            {
                totalSize += new FileInfo(Path.Combine(_appFolder, Path.GetFileName(dll))).Length;
            }

            return totalSize;
        }
    }
}

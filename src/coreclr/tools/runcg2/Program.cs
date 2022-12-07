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
        private Dictionary<string, int>? _compositeAssemblies;
        private int _compositeAssemblyCount;
        private bool _buildFullComposite;

        public static int Main(string[] args)
        {
            return new Program().TryMain(args);
        }

        private int TryMain(string[] args)
        {
            string compositeFileList = "";
            StringBuilder arguments = new StringBuilder();

            for (int argIndex = 0; argIndex < args.Length; argIndex++)
            {
                string arg = args[argIndex];
                if (arg[0] == '-')
                {
                    switch (arg)
                    {
                        case "-dir":
                            _folder = args[++argIndex];
                            break;

                        case "-list":
                            compositeFileList = args[++argIndex];
                            break;

                        case "-count":
                            int.TryParse(args[++argIndex], out _compositeAssemblyCount);
                            break;

                        case "-full":
                            _buildFullComposite = true;
                            break;

                        default:
                            throw new NotImplementedException(arg);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(_app))
                    {
                        _app = arg;
                    }
                    while (++argIndex < args.Length)
                    {
                        arg = args[argIndex];
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
                    break;
                }
            }

            _compositeAssemblies = LoadCompositeAssemblies(compositeFileList, _compositeAssemblyCount >= 0 ? _compositeAssemblyCount : int.MaxValue);

            _commonArguments = arguments.ToString();
            _appFolder = Path.Combine(_folder, "app");

            int totalFiles = 0;
            List<KeyValuePair<string, long>> dllSizes = new List<KeyValuePair<string, long>>();
            foreach (string dll in Directory.EnumerateFiles(_folder, "*.dll"))
            {
                if (IsManagedAssembly(dll))
                {
                    totalFiles++;
                    string simpleName = Path.GetFileNameWithoutExtension(dll);
                    long size;
                    if (_compositeAssemblies != null && _compositeAssemblies.TryGetValue(simpleName, out int line))
                    {
                        size = 1_000_000_000_000_000 - line;
                    }
                    else
                    {
                        size = new FileInfo(dll).Length;
                    }
                    dllSizes.Add(new KeyValuePair<string, long>(dll, size));
                }
            }

            string[] dllsBySize = dllSizes.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();

            List<string> compositeFiles = new List<string>();
            List<string> singleFiles = new List<string>();

            for (int index = 0; index < dllsBySize.Length; index++)
            {
                string dll = dllsBySize[index];
                if (_compositeAssemblies == null ||
                    index < _compositeAssemblyCount ||
                    _compositeAssemblyCount < 0 && index != ~_compositeAssemblyCount)
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
                compositeSize = CompileComposite(compositeFiles, singleFiles);
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

            foreach (string dll in singleFiles)
            {
                Console.WriteLine("### SingleItem: {0} ###", Path.GetFileNameWithoutExtension(dll));
            }

            foreach (string dll in compositeFiles)
            {
                Console.WriteLine("### CompItem: {0} ###", Path.GetFileNameWithoutExtension(dll));
            }

            return _failureCount == 0 ? 0 : 1;
        }

        private static Dictionary<string, int>? LoadCompositeAssemblies(string path, int count)
        {
            if (path == "*")
            {
                return null;
            }
            Dictionary<string, int>? result = new Dictionary<string, int>();
            if (!string.IsNullOrEmpty(path))
            {
                int lineIndex = 0;
                foreach (string line in File.ReadAllLines(path).Take(count))
                {
                    if (!result.ContainsKey(line))
                    {
                        result.Add(line, lineIndex);
                    }
                    lineIndex++;
                }
            }
            return result;
        }

        private void AddCommonCG2Options(StringBuilder responseFileContent, string output)
        {
            responseFileContent.AppendLine("-o:" + output);
            responseFileContent.AppendLine("-O");
            responseFileContent.AppendLine("--mapcsv");
            responseFileContent.AppendLine($"-r:{_folder}\\*.dll");
        }

        private void CompileFile(string dll)
        {
            string output = Path.Combine(_appFolder, Path.GetFileName(dll));
            string responseFile = output + ".rsp";
            StringBuilder responseFileContent = new StringBuilder();
            string fileArgs = _commonArguments + "@" + responseFile;
            AddCommonCG2Options(responseFileContent, output);
            responseFileContent.AppendLine(dll);
            File.WriteAllText(responseFile, responseFileContent.ToString());

            Console.WriteLine("Compiling R2R image {0}", output);
            Console.WriteLine("Running: {0} {1}", _app, fileArgs);
            Console.WriteLine(responseFileContent.ToString());

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

        private long CompileComposite(IEnumerable<string> compositeFiles, IEnumerable<string> singleFiles)
        {
            string compositeName = "composite." + Path.GetFileNameWithoutExtension(compositeFiles.First()) + ".dll";
            string output = Path.Combine(_appFolder, compositeName);
            string responseFile = output + ".rsp";
            string fileArgs = _commonArguments + " @" + responseFile;
            StringBuilder responseFileContent = new StringBuilder();
            AddCommonCG2Options(responseFileContent, output);
            responseFileContent.AppendLine("--composite");
            foreach (string dll in compositeFiles)
            {
                responseFileContent.AppendLine(dll);
            }
            if (_buildFullComposite)
            {
                foreach (string singleDll in singleFiles)
                {
                    responseFileContent.AppendLine("-u:" + singleDll);
                }
            }

            Console.WriteLine("Compiling composite image {0}", output);
            Console.WriteLine("Running: {0} {1}", _app, fileArgs);
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
            foreach (string dll in compositeFiles)
            {
                totalSize += new FileInfo(Path.Combine(_appFolder, Path.GetFileName(dll))).Length;
            }

            return totalSize;
        }
    }
}

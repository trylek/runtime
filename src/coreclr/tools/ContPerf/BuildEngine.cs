// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;

namespace ContPerf
{
    public class PublishInfo
    {
        public int TotalFiles;
        public int CompositeFiles;
        public int SingleFiles;

        public long TotalSize;
        public long CompositeSize;
        public long SingleSize;

        public List<string> SingleAssemblies = new List<string>();
        public List<string> CompositeAssemblies = new List<string>();
    }

    public class ExecutionInfo
    {
        public Statistics StartTimeUsecs = new Statistics();
        public Statistics WorkingSetMB = new Statistics();
        public Statistics PrivateMemoryMB = new Statistics();

        public List<string> JittedMethods = new List<string>();
    }

    public class Statistics
    {
        private long _count;
        private long _sum;
        private long _sumSquared;
        private long _minimum = long.MaxValue;
        private long _maximum;

        public long Count => _count;
        private long SafeCountDenominator => Math.Max(_count, 1);
        public long Average => _sum / SafeCountDenominator;
        public long Minimum => (Count != 0 ? _minimum : 0);
        public long Maximum => _maximum;
        public long Variance => _sumSquared / SafeCountDenominator - Average * Average;
        public long StandardDeviation => (long)Math.Sqrt(Variance);

        public Statistics()
        {
        }

        public Statistics(IEnumerable<long> values)
        {
            Add(values);
        }

        public void Add(long value)
        {
            _count++;
            _sum += value;
            _sumSquared += value * (long)value;
            if (value < _minimum)
            {
                _minimum = value;
            }
            if (value > _maximum)
            {
                _maximum = value;
            }
        }

        public void Add(IEnumerable<long> values)
        {
            foreach (long value in values)
            {
                Add(value);
            }
        }

        public override string ToString()
        {
            return $"{Average} (MIN = {Minimum}, MAX = {Maximum}, STDDEV = {StandardDeviation})";
        }
    }

    public sealed class CsvInfo
    {
        public readonly PublishInfo Publish;
        public readonly ExecutionInfo Stat;

        public CsvInfo(PublishInfo publish, ExecutionInfo stat)
        {
            Publish = publish;
            Stat = stat;
        }

        public static Dictionary<int, CsvInfo> ParseCsvFile(string filename)
        {
            Dictionary<int, CsvInfo> csvMap = new Dictionary<int, CsvInfo>();
            using (StreamReader reader = new StreamReader(filename))
            {
                string? firstLine = reader.ReadLine();
                if (firstLine == null)
                {
                    return csvMap;
                }
                string[] columns = firstLine.Split(',');
                int totalCountIndex = -1;
                int compositeCountIndex = -1;
                int publishSizeIndex = -1;
                int compositeSizeIndex = -1;
                int singleSizeIndex = -1;
                int startupUsecsIndex = -1;
                int workingSetMBIndex = -1;
                int privateMemoryMBIndex = -1;
                for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                {
                    switch (columns[columnIndex])
                    {
                        case "TOTAL":
                            totalCountIndex = columnIndex;
                            break;

                        case "COMPOSITE":
                            compositeCountIndex = columnIndex;
                            break;

                        case "PUBLISH_SIZE":
                            publishSizeIndex = columnIndex;
                            break;

                        case "COMP_SIZE":
                            compositeSizeIndex = columnIndex;
                            break;

                        case "SINGLE_SIZE":
                            singleSizeIndex = columnIndex;
                            break;

                        case "STARTUP_USECS":
                            startupUsecsIndex = columnIndex;
                            break;

                        case "WORKING_SET_MB":
                            workingSetMBIndex = columnIndex;
                            break;

                        case "PRIVATE_MEMORY_MB":
                            privateMemoryMBIndex = columnIndex;
                            break;
                    }
                }

                for (; ; )
                {
                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    string[] parts = line.Split(',');
                    if (parts.Length > 0)
                    {
                        int totalCount = (totalCountIndex >= 0 && totalCountIndex < parts.Length ? int.Parse(parts[totalCountIndex]) : int.MinValue);
                        int compositeCount = (compositeCountIndex >= 0 && compositeCountIndex < parts.Length ? int.Parse(parts[compositeCountIndex]) : int.MinValue);
                        int publishSize = (publishSizeIndex >= 0 && publishSizeIndex < parts.Length ? int.Parse(parts[publishSizeIndex]) : int.MinValue);
                        int compositeSize = (compositeSizeIndex >= 0 && compositeSizeIndex < parts.Length ? int.Parse(parts[compositeSizeIndex]) : int.MinValue);
                        int singleSize = (singleSizeIndex >= 0 && singleSizeIndex < parts.Length ? int.Parse(parts[singleSizeIndex]) : int.MinValue);

                        ExecutionInfo stat = new ExecutionInfo();
                        if (startupUsecsIndex >= 0 && startupUsecsIndex < parts.Length)
                        {
                            stat.StartTimeUsecs.Add(int.Parse(parts[startupUsecsIndex]));
                        }
                        if (workingSetMBIndex >= 0 && workingSetMBIndex < parts.Length)
                        {
                            stat.WorkingSetMB.Add(int.Parse(parts[workingSetMBIndex]));
                        }
                        if (privateMemoryMBIndex >= 0 && privateMemoryMBIndex < parts.Length)
                        {
                            stat.PrivateMemoryMB.Add(int.Parse(parts[privateMemoryMBIndex]));
                        }
                        if (compositeCount > 0 || publishSize > 0 || compositeSize > 0 || singleSize > 0)
                        {
                            PublishInfo pubInfo = new PublishInfo()
                            {
                                TotalFiles = totalCount,
                                CompositeFiles = compositeCount,
                                SingleFiles = totalCount - compositeCount,
                                TotalSize = publishSize,
                                CompositeSize = compositeSize,
                                SingleSize = singleSize,
                            };
                            csvMap.Add(compositeCount, new CsvInfo(pubInfo, stat));
                        }
                    }
                }
            }
            return csvMap;
        }
    }

    public sealed class BuildEngine
    {
        private readonly string _crossgen2Path;
        private readonly bool _useLinux;
        private readonly bool _useContainers;
        private readonly bool _buildFullComposite;
        private readonly bool _emitMapFile;
        private readonly bool _useCrossModuleInlining;
        private readonly bool _useHotColdSplitting;
        private readonly string _publishDir;
        private readonly string _appDir;
        private readonly string _appCrankDir;
        private readonly string _compositeFileList;
        private readonly int _compositeFileCount;
        private readonly TextWriter _buildLogWriter;
        private readonly string _mibcFile;
        private readonly HashSet<string> _assemblySkipList;

        private readonly Dictionary<string, int> _compositeAssemblies;
        private readonly List<string> _compositeFiles;
        private readonly List<string> _singleFiles;

        private string? _compositeFileName;

        public BuildEngine(
            string crossgen2Path,
            bool useLinux,
            bool useContainers,
            bool buildFullComposite,
            bool emitMapFile,
            bool useCrossModuleInlining,
            bool useHotColdSplitting,
            string publishDir,
            string appDir,
            string compositeFileList,
            int compositeFileCount,
            HashSet<string> assemblySkipList,
            TextWriter buildLogWriter,
            string mibcFile)
        {
            _crossgen2Path = crossgen2Path;
            _useLinux = useLinux;
            _useContainers = useContainers;
            _buildFullComposite = buildFullComposite;
            _emitMapFile = emitMapFile;
            _useCrossModuleInlining = useCrossModuleInlining;
            _useHotColdSplitting= useHotColdSplitting;
            _publishDir = publishDir;
            _appDir = appDir;
            _appCrankDir = Path.Combine(_appDir, "published");
            _compositeFileList = compositeFileList;
            _compositeFileCount = compositeFileCount;
            _buildLogWriter = buildLogWriter;
            _mibcFile = mibcFile;
            _assemblySkipList = assemblySkipList;

            _compositeAssemblies = new Dictionary<string, int>();
            _compositeFiles = new List<string>();
            _singleFiles = new List<string>();
        }

        public void Build(out PublishInfo publishInfo)
        {
            PrepareAppFolder();
            LoadCompositeAssemblies();
            SelectAssembliesForCompilation();

            if (_useCrossModuleInlining || _useHotColdSplitting)
            {
                Parallel.ForEach(_singleFiles, (dll) =>
                    {
                        RunCrossgen2(
                            composite: false,
                            output: Path.Combine(_appCrankDir, Path.GetFileName(dll)),
                            inputs: new string[] { dll },
                            unrootedInputs: Array.Empty<string>());
                    }
                );
            }
            if (!_useCrossModuleInlining && (_compositeFiles.Count > 0 || _buildFullComposite))
            {
                CompileCompositeImage();
            }

            publishInfo = new PublishInfo();
            publishInfo.SingleFiles = _singleFiles.Count;
            publishInfo.CompositeFiles = _compositeFiles.Count;
            publishInfo.SingleAssemblies = _singleFiles;
            publishInfo.CompositeAssemblies = _compositeFiles;
            publishInfo.TotalFiles = _singleFiles.Count + _compositeFiles.Count;
            publishInfo.SingleSize = _singleFiles.Sum(f => new FileInfo(Path.Combine(_appCrankDir, Path.GetFileName(f))).Length);
            publishInfo.CompositeSize = _compositeFiles.Sum(f => new FileInfo(Path.Combine(_appCrankDir, Path.GetFileName(f))).Length)
                + (_compositeFileName != null ? new FileInfo(_compositeFileName).Length : 0);
            publishInfo.TotalSize = publishInfo.SingleSize + publishInfo.CompositeSize;
        }

        private void PrepareAppFolder()
        {
            HashSet<string> publishFolders = new HashSet<string>();
            HashSet<string> appFolders = new HashSet<string>();
            HashSet<string> publishFiles = new HashSet<string>();
            HashSet<string> appFiles = new HashSet<string>();

            Directory.CreateDirectory(_appCrankDir);

            foreach (string folder in Directory.EnumerateDirectories(_publishDir, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(_publishDir, folder);
                if (relativePath.StartsWith("app\\published\\"))
                {
                    appFolders.Add(relativePath.Substring(14));
                }
                else if (!relativePath.StartsWith("logs\\") && relativePath != "app" && relativePath != "logs" && !relativePath.StartsWith("app\\published"))
                {
                    publishFolders.Add(relativePath);
                }
            }

            foreach (string file in Directory.EnumerateFiles(_publishDir, "*.*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(_publishDir, file);
                if (relativePath.StartsWith("app\\published\\"))
                {
                    appFiles.Add(relativePath.Substring(14));
                }
                else if (!relativePath.StartsWith("logs\\") && !relativePath.StartsWith("app\\published"))
                {
                    publishFiles.Add(relativePath);
                }
            }

            // Remove extra files
            foreach (string extraFile in appFiles.Where(af => !publishFiles.Contains(af)))
            {
                string extraFilePath = Path.Combine(_appCrankDir, extraFile);
                Console.WriteLine("Deleting extra file {0}", extraFilePath);
                File.Delete(extraFilePath);
            }

            // Remove extra folders
            foreach (string extraFolder in appFolders.Where(af => !publishFolders.Contains(af)))
            {
                string extraFolderPath = Path.Combine(_appCrankDir, extraFolder);
                Console.WriteLine("Deleting extra folder {0}", extraFolderPath);
                Directory.Delete(extraFolderPath);
            }

            // Create non-existent folders
            foreach (string folder in publishFolders)
            {
                string folderPath = Path.Combine(_appCrankDir, folder);
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
            }

            // Copy files using hardlinks
            foreach (string file in publishFiles)
            {
                string publishPath = Path.Combine(_publishDir, file);
                string appPath = Path.Combine(_appCrankDir, file);
                File.Copy(publishPath, appPath, overwrite: true);
            }
        }

        private void LoadCompositeAssemblies()
        {
            if (!string.IsNullOrEmpty(_compositeFileList))
            {
                int lineIndex = 0;
                foreach (string line in File.ReadAllLines(_compositeFileList))
                {
                    _compositeAssemblies.TryAdd(line, lineIndex);
                    lineIndex++;
                }
            }
        }

        private void SelectAssembliesForCompilation()
        {
            int totalFiles = 0;
            List<KeyValuePair<string, long>> dllSizes = new List<KeyValuePair<string, long>>();
            foreach (string dll in Directory.EnumerateFiles(_publishDir, "*.dll"))
            {
                if (IsManagedAssembly(dll))
                {
                    totalFiles++;
                    string simpleName = Path.GetFileNameWithoutExtension(dll);
                    if (_assemblySkipList.Contains(simpleName))
                    {
                        continue;
                    }
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

            for (int index = 0; index < dllsBySize.Length; index++)
            {
                string dll = dllsBySize[index];
                if (!_useCrossModuleInlining &&
                    (_compositeAssemblies == null ||
                        index < _compositeFileCount ||
                        _compositeFileCount < 0 && index != ~_compositeFileCount))
                {
                    _compositeFiles.Add(dll);
                }
                else
                {
                    _singleFiles.Add(dll);
                }
            }
        }

        private void CompileCompositeImage()
        {
            string compositeName = "composite." + Path.GetFileNameWithoutExtension(_compositeFiles.Count > 0 ? _compositeFiles[0] : _singleFiles[0]) + ".dll";
            _compositeFileName = Path.Combine(_appCrankDir, compositeName);
            Console.WriteLine("Compiling composite image {0}", _compositeFileName);
            RunCrossgen2(
                composite: true,
                output: _compositeFileName,
                inputs: _compositeFiles,
                unrootedInputs: _singleFiles);
        }

        private void RunCrossgen2(
            bool composite,
            string output,
            IEnumerable<string> inputs,
            IEnumerable<string> unrootedInputs)
        {
            string responseFile = output + ".rsp";
            string fileArgs = "@" + responseFile;
            StringBuilder responseFileContent = new StringBuilder();

            responseFileContent.AppendLine("--targetos:" + (_useLinux ? "linux" : "windows"));
            responseFileContent.AppendLine("-o:" + output);
            responseFileContent.AppendLine("-O");

            if (_mibcFile != "")
            {
                responseFileContent.AppendLine("--mibc:" + _mibcFile);
                // responseFileContent.AppendLine("--partial");
            }

            if (_useCrossModuleInlining)
            {
                responseFileContent.AppendLine("--opt-cross-module:*");
                responseFileContent.AppendLine("--opt-async-methods");
            }
            if (_useHotColdSplitting)
            {
                responseFileContent.AppendLine("--hot-cold-splitting");
                responseFileContent.AppendLine("--method-layout:hotwarmcold");
            }
            if (_emitMapFile)
            {
                responseFileContent.AppendLine("--mapcsv");
            }
            responseFileContent.AppendLine($"-r:{_publishDir}\\*.dll");
            if (composite)
            {
                responseFileContent.AppendLine("--composite");
            }

            foreach (string dll in inputs)
            {
                responseFileContent.AppendLine(dll);
            }

            if (_buildFullComposite)
            {
                foreach (string singleDll in unrootedInputs)
                {
                    responseFileContent.AppendLine("-u:" + singleDll);
                }
                string? lastUnrootedInput = unrootedInputs.LastOrDefault();
                if (!string.IsNullOrEmpty(lastUnrootedInput))
                {
                    responseFileContent.AppendLine(lastUnrootedInput);
                }
            }

            Console.WriteLine("Running: {0} {1}", _crossgen2Path, fileArgs);
            Console.WriteLine(responseFileContent.ToString());
            File.WriteAllText(responseFile, responseFileContent.ToString());

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = _crossgen2Path,
                Arguments = fileArgs,
            };

            using (Process cg2Process = Process.Start(psi)!)
            {
                cg2Process.WaitForExit();
                if (cg2Process.ExitCode != 0)
                {
                    throw new Exception($"Error compiling composite image '{output}'");
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


        /*
        private static bool TryReadCG2Value(string line, string tag, ref int value)
        {
            string stringValue = "";
            bool result = TryReadCG2Value(line, tag, ref stringValue);
            if (result)
            {
                value = int.Parse(stringValue);
            }
            return result;
        }

        private static bool TryReadCG2Value(string line, string tag, ref string value)
        {
            int tagIndex = line.IndexOf(tag);
            if (tagIndex >= 0)
            {
                int start = tagIndex + tag.Length;
                int end = line.Length;
                while (end > start && line[end - 1] == '#')
                {
                    end--;
                }
                while (end > start && char.IsWhiteSpace(line[end - 1]))
                {
                    end--;
                }

                value = line.Substring(start, end - start);
                return true;
            }
            return false;
        }
        */
    }
}

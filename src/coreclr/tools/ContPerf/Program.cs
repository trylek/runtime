// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Xml;

namespace ContPerf
{
    public class PublishInfo
    {
        public int Size;
        public int TotalFiles;
        public int CompositeFiles;
        public int CompositeSize;
        public int SingleFiles;
        public int SingleSize;

        public List<string> SingleAssemblies = new List<string>();
        public List<string> CompositeAssemblies = new List<string>();
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
    }

    public sealed class Program
    {
        private const string LinuxImageString = "writing image sha256:";
        private const string WindowsImageString = "Successfully built ";
        private const string CompositeFilesString = "### Composite:  ";
        private const string CompositeSizeString = "### CompSize:   ";
        private const string SingleFilesString = "### Singlefile: ";
        private const string SingleSizeString = "### SingleSize: ";
        private const string TotalFilesString = "### Total dlls: ";
        private const string R2RLengthString = "### R2R-length: ";
        private const string SingleItemString = "### SingleItem: ";
        private const string CompositeItemString = "### CompItem: ";
        private const string JitCompiledTag = ": JIT compiled";
        private const int WarmupIterations = 2;
        private const int DefaultIterations = 50;

        private static bool s_useLinux;
        private static bool s_useReadyToRun = true;
        private static bool s_useTieredCompilation;
        private static bool s_useContainers = true;
        private static bool s_usePartialComposite;
        private static bool s_measureNegativeComposite;
        private static bool s_useFastMode;
        private static int? s_partialIndex;
        private static int s_iterations = DefaultIterations;

        private enum NextArg
        {
            Command,
            CompositeFileList,
            PartialIndex,
            Iterations,
        }

        private static string? s_compositeFileList;

        private static string[] s_buildModes =
        {
            "default-r2r",
            "runtime.composite",
            "runtime+asp.net.composite",
            "runtime.composite+asp.net.composite",
            "full.composite",
            "cross-module-inlining",
        };

        private static string s_folderName = "";
        private static string s_publishFolderName = "";

        private static string? s_timestamp;

        private static TextWriter? s_buildLogFile;
        private static TextWriter? s_execLogFile;
        private static TextWriter? s_resultsCsvFile;

        public static int Main(string[] args)
        {
            NextArg nextArg = NextArg.Command;

            foreach (string arg in args)
            {
                switch (nextArg)
                {
                    case NextArg.CompositeFileList:
                        s_compositeFileList = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.PartialIndex:
                        s_partialIndex = int.Parse(arg);
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.Iterations:
                        s_iterations = int.Parse(arg);
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.Command:
                        switch (arg)
                        {
                            case "LINUX":
                                s_useLinux = true;
                                break;

                            case "WINDOWS":
                                s_useLinux = false;
                                break;

                            case "NORTR":
                                s_useReadyToRun = false;
                                break;

                            case "TIER":
                                s_useTieredCompilation = true;
                                break;

                            case "NOCONT":
                                s_useContainers = false;
                                break;

                            case "PARTIAL":
                                s_usePartialComposite = true;
                                nextArg = NextArg.CompositeFileList;
                                break;

                            case "NEGATIVE":
                                s_measureNegativeComposite = true;
                                break;

                            case "INDEX":
                                nextArg = NextArg.PartialIndex;
                                break;

                            case "ITERATIONS":
                                nextArg = NextArg.Iterations;
                                break;

                            case "FAST":
                                s_useFastMode = true;
                                break;

                            default:
                                throw new Exception($"Unsupported argument '{arg}'");
                        }
                        break;

                    default:
                        throw new NotImplementedException(nextArg.ToString());
                }
            }

            string rid = (s_useLinux ? "linux" : "win") + "-x64";

            s_timestamp = DateTime.Now.ToString("MMdd-HHmm");
            s_folderName = Directory.GetCurrentDirectory();
            s_publishFolderName = Path.Combine(s_folderName, "bin", "Release", "net7.0", rid, "publish", "app");

            string buildLogFile = Path.Combine(s_folderName, $"build-{s_timestamp}.log");
            string execLogFile = Path.Combine(s_folderName, $"run-{s_timestamp}.log");
            string resultsCsvFile = Path.Combine(s_folderName, $"results-{s_timestamp}.csv");

            Statistics[] results = new Statistics[s_buildModes.Length];
            List<string>[] jitMethods = new List<string>[s_buildModes.Length];

            using (StreamWriter buildLogWriter = new StreamWriter(buildLogFile))
            using (StreamWriter execLogWriter = new StreamWriter(execLogFile))
            using (StreamWriter resultsCsvWriter = new StreamWriter(resultsCsvFile))
            {
                s_buildLogFile = buildLogWriter;
                s_execLogFile = execLogWriter;
                s_resultsCsvFile = resultsCsvWriter;
                if (s_usePartialComposite)
                {
                    MeasurePartialComposite();
                }
                else
                {
                    for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                    {
                        BuildAndRun(s_buildModes[modeIndex], modeIndex, s_buildModes.Length,
                            compositeFileList: "", compositeFileCount: 0, out results[modeIndex], out jitMethods[modeIndex],
                            out PublishInfo publishInfo);
                    }

                    s_execLogFile.WriteLine("   COUNT |     AVG% |      AVG |      JIT | MODE");
                    s_execLogFile.WriteLine("------------------------------------------------");
                    for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                    {
                        Statistics result = results[modeIndex];
                        long averagePercentage = (result.Average * 100L / Math.Max(results[0].Average, 1));
                        s_execLogFile.WriteLine("{0,8} | {1,8} | {2,8} | {3,8} | {4}",
                            result.Count,
                            averagePercentage,
                            result.Average,
                            jitMethods[modeIndex].Count,
                            s_buildModes[modeIndex]);
                    }
                }

                s_buildLogFile = null;
                s_execLogFile = null;
                s_resultsCsvFile = null;
            }

            Console.WriteLine("Build log:     {0}", buildLogFile);
            Console.WriteLine("Execution log: {0}", execLogFile);
            Console.WriteLine("Results xls:   {0}", resultsCsvFile);

            return 0;
        }

        private static void MeasurePartialComposite()
        {
            if (Build("init", useContainers: false, 0, 0, compositeFileList: "", compositeFileCount: 0, out PublishInfo _) == null)
            {
                throw new Exception("App publishing failed");
            }

            if (s_partialIndex.HasValue)
            {
                BuildPartialCompositeAtIndex($"default-r2r", s_compositeFileList!, s_partialIndex.Value);
                return;
            }

            if (s_measureNegativeComposite)
            {
                MeasureNegativeComposite();
                return;
            }

            const string buildMode = "default-r2r";
            BuildAndRun(buildMode, 0, 1, "", 0, out Statistics firstPartialStat, out List<string> firstPartialMethods, out PublishInfo firstPublishInfo);
            int totalFiles = firstPublishInfo.TotalFiles;
            Statistics[] statistics = new Statistics[totalFiles + 1];
            int[] jitMethodCount = new int[totalFiles + 1];
            PublishInfo[] publishInfos = new PublishInfo[totalFiles + 1];
            bool[] calculated = new bool[totalFiles + 1];

            statistics[0] = firstPartialStat;
            jitMethodCount[0] = firstPartialMethods.Count;
            publishInfos[0] = firstPublishInfo;
            calculated[0] = true;

            // Build full composite
            BuildAndRun(buildMode, totalFiles, totalFiles,
                compositeFileList: "*", compositeFileCount: 1, out Statistics fullStat, out List<string> fullMethods, out PublishInfo fullPublishInfo);
            statistics[totalFiles] = fullStat;
            jitMethodCount[totalFiles] = fullMethods.Count;
            publishInfos[totalFiles] = fullPublishInfo;
            calculated[totalFiles] = true;

            BisectPartialComposite(buildMode, totalFiles, s_compositeFileList!, statistics, jitMethodCount, publishInfos, calculated, 0, totalFiles);

            s_execLogFile!.WriteLine("#COMPOSITE | PUBLISH SIZE | COMP-SIZE | SINGLE-SIZE | JIT COUNT | STARTUP USECS |       MIN |       MAX |    STDDEV");
            s_execLogFile!.WriteLine("-------------------------------------------------------------------------------------------------------------------");
            s_resultsCsvFile!.WriteLine("COMPOSITE,PUBLISH_SIZE,COMP_SIZE,SINGLE_SIZE,JIT_COUNT,STARTUP_USECS,MIN,MAX,STDDEV");
            for (int index = 0; index <= totalFiles; index++)
            {
                PublishInfo info = publishInfos[index];
                Statistics stat = statistics[index];
                if (stat != null)
                {
                    s_execLogFile!.WriteLine(
                        "{0,10} | {1,12} | {2,9} | {3,11} | {4,9} | {5,13} | {6,9} | {7,9} | {8,9}",
                        index,
                        info.Size,
                        info.CompositeSize,
                        info.SingleSize,
                        jitMethodCount[index],
                        stat.Average,
                        stat.Minimum,
                        stat.Maximum,
                        stat.StandardDeviation);

                    s_resultsCsvFile!.WriteLine(
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                        index,
                        info.Size,
                        info.CompositeSize,
                        info.SingleSize,
                        jitMethodCount[index],
                        stat.Average,
                        stat.Minimum,
                        stat.Maximum,
                        stat.StandardDeviation);
                }
            }
        }

        private static void MeasureNegativeComposite()
        {
            // Build full composite
            BuildAndRun("default-r2r", 1, 1,
                compositeFileList: "*", compositeFileCount: 1,
                out Statistics fullStat,
                out List<string> fullMethods,
                out PublishInfo fullPublishInfo);
            int totalFiles = fullPublishInfo.CompositeFiles;
            if (s_useFastMode)
            {
                totalFiles = Math.Min(totalFiles, 10);
            }
            PublishInfo[] publishInfo = new PublishInfo[totalFiles];
            Statistics[] statistics = new Statistics[totalFiles];
            List<string>[] jitMethods = new List<string>[totalFiles];

            for (int i = 0; i < totalFiles; i++)
            {
                BuildAndRun("default-r2r", i, totalFiles,
                    s_compositeFileList!, compositeFileCount: ~i,
                    out statistics[i], out jitMethods[i], out publishInfo[i]);
            }

            s_resultsCsvFile!.WriteLine("INDEX,PUBLISH_SIZE,STARTUP_USECS,JIT_COUNT,SIZE_DELTA,STARTUP_DELTA,JIT_DELTA,EXCLUDED_ASSEMBLY,");
            for (int i = 0; i < totalFiles; i++)
            {
                PublishInfo info = publishInfo[i];
                Statistics stat = statistics[i];
                List<string> methods = jitMethods[i];
                s_resultsCsvFile!.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7},",
                    i,
                    info.Size,
                    stat.Minimum,
                    methods.Count,
                    fullPublishInfo.Size - info.Size,
                    stat.Minimum - fullStat.Minimum,
                    methods.Count - fullMethods.Count,
                    info.SingleAssemblies.FirstOrDefault());
            }
        }


        private static void BuildPartialCompositeAtIndex(string buildMode, string compositeFileList, int compositeFileIndex)
        {
            BuildAndRun(buildMode, 1, 1,
                compositeFileList, compositeFileIndex,
                out Statistics stat, out List<string> jitMethods, out PublishInfo publishInfo);

            s_buildLogFile!.WriteLine("Composite file list: {0}", compositeFileList);
            s_buildLogFile!.WriteLine("Composite index:     {0}", compositeFileIndex);
            s_buildLogFile!.WriteLine("Publish size:        {0}", publishInfo.Size);
            s_buildLogFile!.WriteLine("Composite size:      {0}", publishInfo.CompositeSize);
            s_buildLogFile!.WriteLine("Single size:         {0}", publishInfo.SingleSize);
            s_buildLogFile!.WriteLine("Total files:         {0}", publishInfo.TotalFiles);
            s_buildLogFile!.WriteLine("Composite files:     {0}", publishInfo.CompositeFiles);
            s_buildLogFile!.WriteLine("Single files:        {0}", publishInfo.SingleFiles);
            s_buildLogFile!.WriteLine("Startup time AVG:    {0}", stat.Average);
            s_buildLogFile!.WriteLine("Startup time MIN:    {0}", stat.Minimum);
            s_buildLogFile!.WriteLine("Startup time MAX:    {0}", stat.Maximum);
            s_buildLogFile!.WriteLine("Startup time STDDEV: {0}", stat.StandardDeviation);
            s_buildLogFile!.WriteLine("Runtime JIT count:   {0}", jitMethods.Count);
            s_buildLogFile!.WriteLine("Precise JIT count:   {0}", jitMethods.Count(m => m != ""));

            Dictionary<string, int> methodCounts = new Dictionary<string, int>();
            foreach (string method in jitMethods.Where(m => m != ""))
            {
                methodCounts.TryGetValue(method, out int count);
                methodCounts[method] = count + 1;
            }

            s_buildLogFile!.WriteLine("JITted multiple times:");

            s_buildLogFile!.WriteLine("COUNT | METHOD");
            s_buildLogFile!.WriteLine("--------------");
            foreach (KeyValuePair<string, int> kvp in methodCounts.Where(mc => mc.Value > 1).OrderByDescending(mc => mc.Value))
            {
                s_buildLogFile!.WriteLine("{0,5} | {1}", kvp.Value, kvp.Key);
            }

            s_buildLogFile!.WriteLine("Unique JIT count:    {0}", methodCounts.Count);

            foreach (string jitMethod in methodCounts.Keys.OrderBy(m => m))
            {
                s_buildLogFile!.WriteLine("    {0}", jitMethod);
            }
        }

        private static string s_separator = new string('-', 70);

        private static int s_lastProgress;

        private static void ShowProgress(bool[] calculated)
        {
            int done = calculated.Sum(c => c ? 1 : 0);
            if (done > s_lastProgress)
            {
                s_lastProgress = done;
                Console.WriteLine(s_separator);
                Console.WriteLine("Completed {0} / {1} ({2:F1}%)", done, calculated.Length, done * 100.0 / calculated.Length);
                Console.WriteLine(s_separator);
            }
        }

        private static void BisectPartialComposite(
            string buildMode, int totalFiles, string compositeFileList,
            Statistics[] statistics, int[] jitMethodCount, PublishInfo[] publishInfo, bool[] calculated, int low, int high)
        {
            ShowProgress(calculated);

            if (high <= low + 1)
            {
                return;
            }

            Statistics lowStat = statistics[low];
            Statistics highStat = statistics[high];
            PublishInfo lowPublish = publishInfo[low];
            PublishInfo highPublish = publishInfo[high];

            bool bisect = false;
            if (Math.Abs(highStat.Average - lowStat.Average) >= 10000)
            {
                bisect = true;
            }
            if (Math.Abs(highPublish.Size - lowPublish.Size) >= 500000)
            {
                bisect = true;
            }
            if (s_useFastMode && high - low <= 20)
            {
                bisect = false;
            }

            if (bisect)
            {
                int middle = (high + low) >> 1;
                BuildAndRun(buildMode, middle, totalFiles,
                    compositeFileList, middle,
                    out Statistics middleStat, out List<string> middleMethods, out PublishInfo middlePublishInfo);
                statistics[middle] = middleStat;
                jitMethodCount[middle] = middleMethods.Count;
                publishInfo[middle] = middlePublishInfo;
                calculated[middle] = true;

                BisectPartialComposite(buildMode, totalFiles, compositeFileList,
                    statistics, jitMethodCount, publishInfo, calculated,
                    low, middle);
                BisectPartialComposite(buildMode, totalFiles, compositeFileList,
                    statistics, jitMethodCount, publishInfo, calculated,
                    middle, high);
            }
            else
            {
                for (int index = low; ++index < high;)
                {
                    calculated[index] = true;
                }
            }

            ShowProgress(calculated);
        }

        private static void BuildAndRun(in string buildMode, int index, int count,
            string compositeFileList, int compositeFileCount, out Statistics stat, out List<string> jitMethods,
            out PublishInfo publishInfo)
        {
            string? image = Build(buildMode, s_useContainers, index, count,
                compositeFileList, compositeFileCount, out publishInfo);
            if (image == null || s_iterations == 0)
            {
                stat = new Statistics();
                jitMethods = new List<string>();
                return;
            }
            Run(buildMode, image, useTieredCompilation: s_useTieredCompilation, useReadyToRun: s_useReadyToRun, out stat, out jitMethods);
        }

        private static string? Build(in string buildMode, bool useContainers, int index, int total,
            in string compositeFileList, int compositeFileCount, out PublishInfo publishInfo)
        {
            Stopwatch sw = Stopwatch.StartNew();
            s_buildLogFile!.WriteLine("Building configuration: {0} ({1} / {2})", buildMode, index, total);

            StringBuilder buildArgs = new StringBuilder();
            buildArgs.Append(s_useLinux ? "linux" : "win");
            buildArgs.Append(' ');
            buildArgs.Append(buildMode);
            if (!string.IsNullOrEmpty(compositeFileList))
            {
                buildArgs.Append(' ');
                buildArgs.Append(compositeFileList);
                buildArgs.Append(' ');
                buildArgs.Append(compositeFileCount);
            }

            ProcessStartInfo psiBuildCmd = new ProcessStartInfo()
            {
                FileName = Path.Combine(s_folderName!, "build.cmd"),
                Arguments = buildArgs.ToString(),
                UseShellExecute = false,
            };
            psiBuildCmd.Environment["UseContainers"] = useContainers ? "1" : "0";

            int exitCode = RunProcess(psiBuildCmd, s_buildLogFile!, out List<string> stdout);
            publishInfo = new PublishInfo();

            string? imageId = null;
            if (exitCode == 0)
            {
                for (int i = stdout.Count - 1; i >= 0; i--)
                {
                    string line = stdout[i];

                    TryReadCG2Value(line, R2RLengthString, ref publishInfo.Size);
                    TryReadCG2Value(line, TotalFilesString, ref publishInfo.TotalFiles);
                    TryReadCG2Value(line, CompositeFilesString, ref publishInfo.CompositeFiles);
                    TryReadCG2Value(line, CompositeSizeString, ref publishInfo.CompositeSize);
                    TryReadCG2Value(line, SingleFilesString, ref publishInfo.SingleFiles);
                    TryReadCG2Value(line, SingleSizeString, ref publishInfo.SingleSize);
                    string singleItem = "";
                    if (TryReadCG2Value(line, SingleItemString, ref singleItem))
                    {
                        publishInfo.SingleAssemblies.Add(singleItem);
                    }
                    string compositeItem = "";
                    if (TryReadCG2Value(line, CompositeItemString, ref compositeItem))
                    {
                        publishInfo.CompositeAssemblies.Add(compositeItem);
                    }
                }

                if (useContainers)
                {
                    for (int i = stdout.Count - 1; i >= 0 && i >= stdout.Count - 10; i--)
                    {
                        string line = stdout[i];
                        int writingImage = line.IndexOf(LinuxImageString);
                        int skipOffset = LinuxImageString.Length;
                        if (writingImage < 0)
                        {
                            writingImage = line.IndexOf(WindowsImageString);
                            skipOffset = WindowsImageString.Length;
                        }
                        if (writingImage >= 0)
                        {
                            imageId = line.Substring(writingImage + skipOffset);
                            int blank = imageId.IndexOf(' ');
                            if (blank >= 0)
                            {
                                imageId = imageId.Substring(0, blank);
                            }
                            break;
                        }
                    }
                }
                else
                {
                    imageId = "";
                }
            }
            s_buildLogFile!.WriteLine("Done building configuration: {0} ({1} / {2}, {3} msecs)", buildMode, index, total, sw.ElapsedMilliseconds);
            return imageId;

            /*
            string outputDir = @"D:\triage\composite\webapi\bin\Release\net6.0\win-x64\publish";
            if (buildMode.NetCoreComposite)
            {
                outputDir = Path.Combine(outputDir, "composite");
            }

            return Path.Combine(outputDir, "webapi.dll");
            */
        }

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

        private static int Execute(
            string dockerImageId,
            bool collectMethods,
            bool useTieredCompilation,
            bool useReadyToRun,
            TextWriter logFile,
            out List<string> stdout)
        {
            List<KeyValuePair<string, string>> environment = new List<KeyValuePair<string, string>>();
            environment.Add(new KeyValuePair<string, string>("DOTNET_TieredCompilation", useTieredCompilation ? "1" : "0"));
            if (collectMethods)
            {
                environment.Add(new KeyValuePair<string, string>("DOTNET_JitDisasmSummary", "1"));
                environment.Add(new KeyValuePair<string, string>("TotalIterations", "1"));
            }
            else
            {
                environment.Add(new KeyValuePair<string, string>("TotalIterations", (WarmupIterations + s_iterations).ToString()));
            }
            environment.Add(new KeyValuePair<string, string>("DOTNET_ReadyToRun", useReadyToRun ? "1" : "0"));

            string runScriptName = "runapp." + (s_useLinux ? "sh" : "cmd");
            string application;
            StringBuilder commandLine = new StringBuilder();
            if (s_useContainers)
            {
                application = "docker";
                commandLine.Append("run");

                foreach (KeyValuePair<string, string> kvpEnv in environment)
                {
                    commandLine.Append(" --env ");
                    commandLine.Append(kvpEnv.Key);
                    commandLine.Append('=');
                    commandLine.Append(kvpEnv.Value);
                }

                commandLine.AppendFormat(" -it {0}", dockerImageId);

                if (s_useLinux)
                {
                    commandLine.Append(" /app/");
                }
                else
                {
                    commandLine.Append(" c:\\app\\");
                }
                commandLine.Append(runScriptName);
            }
            else
            {
                application = Path.Combine(s_publishFolderName, runScriptName);
            }
            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = application,
                Arguments = commandLine.ToString(),
                UseShellExecute = false,
            };
            if (!s_useContainers)
            {
                foreach (KeyValuePair<string, string> kvpEnv in environment)
                {
                    psi.Environment[kvpEnv.Key] = kvpEnv.Value;
                }
            }
            return RunProcess(psi, logFile, out stdout);
        }

        private static void Run(string buildMode, string dockerImageId, bool useTieredCompilation, bool useReadyToRun,
            out Statistics stat, out List<string> jitMethods)
        {
            s_execLogFile!.WriteLine("Running configuration: {0}", buildMode);

            jitMethods = new List<string>();
            stat = new Statistics();

            int exitCode;
            List<string> stdout;

            exitCode = Execute(dockerImageId, collectMethods: true, useTieredCompilation: useTieredCompilation, useReadyToRun: useReadyToRun,
                s_execLogFile!, out stdout);
            if (exitCode != 0)
            {
                return;
            }

            for (int lineIndex = 0; lineIndex < stdout.Count; lineIndex++)
            {
                string line = stdout[lineIndex];
                int jitIndex = line.IndexOf(JitCompiledTag);
                if (jitIndex > 0)
                {
                    int methodPos = jitIndex + JitCompiledTag.Length;
                    int numberEnd = jitIndex;
                    while (jitIndex > 0 && char.IsDigit(line[jitIndex - 1]))
                    {
                        jitIndex--;
                    }
                    int numberBegin = jitIndex;
                    if (int.TryParse(line.AsSpan(numberBegin, numberEnd - numberBegin), out int methodIndex))
                    {
                        while (methodPos < line.Length && char.IsWhiteSpace(line[methodPos]))
                        {
                            methodPos++;
                        }

                        int methodEnd = methodPos;
                        while (methodEnd < line.Length && line[methodEnd] >= ' ')
                        {
                            methodEnd++;
                        }

                        while (jitMethods.Count <= methodIndex)
                        {
                            jitMethods.Add("");
                        }

                        jitMethods[methodIndex] = line.Substring(methodPos, methodEnd - methodPos);
                    }
                }
            }

            exitCode = Execute(dockerImageId, collectMethods: false, useTieredCompilation: useTieredCompilation, useReadyToRun: useReadyToRun,
                s_execLogFile!,
                out stdout);
            if (exitCode != 0)
            {
                return;
            }

            List<long> usecDurations = new List<long>();
            int warmupIterationsLeft = WarmupIterations;
            for (int lineIndex = 0; lineIndex < stdout.Count; lineIndex++)
            {
                string line = stdout[lineIndex];
                const string USecsTag = "### USECS: ";
                if (warmupIterationsLeft > 0)
                {
                    // Skip initial measurements corresponding to the warmup iterations
                    warmupIterationsLeft--;
                    continue;
                }
                int tagIndex = line.IndexOf(USecsTag);
                if (tagIndex >= 0)
                {
                    int start = tagIndex + USecsTag.Length;
                    int iterationEnd = line.IndexOf('/', start);
                    if (iterationEnd > start)
                    {
                        int valueEnd = line.IndexOf('#', iterationEnd);
                        if (valueEnd > iterationEnd)
                        {
                            int iteration = int.Parse(line.AsSpan(start, iterationEnd - start));
                            if (iteration >= usecDurations.Count)
                            {
                                long duration = long.Parse(line.AsSpan(iterationEnd + 1, valueEnd - iterationEnd - 1));
                                usecDurations.Add(duration);
                            }
                        }
                    }
                }
            }
            stat = new Statistics(usecDurations);
            s_execLogFile!.WriteLine("JITTED:  {0}", jitMethods.Count);
            s_execLogFile!.WriteLine("COUNT:   {0}", stat.Count);
            s_execLogFile!.WriteLine("AVERAGE: {0}", stat.Average);
            s_execLogFile!.WriteLine("MINIMUM: {0}", stat.Minimum);
            s_execLogFile!.WriteLine("MAXIMUM: {0}", stat.Maximum);
            s_execLogFile!.WriteLine("STDDEV:  {0}", stat.StandardDeviation);
        }

        private static int RunProcess(ProcessStartInfo psi, TextWriter logFile, out List<string> stdout)
        {
            logFile.WriteLine("RunProcess: {0} {1}", psi.FileName, psi.Arguments);

            Stopwatch sw = Stopwatch.StartNew();

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                process.StartInfo.RedirectStandardOutput = !psi.UseShellExecute;
                process.StartInfo.RedirectStandardError = !psi.UseShellExecute;
                process.StartInfo.Environment["DOCKER_BUILDKIT"] = "1";

                logFile.WriteLine("Running {0} {1}", psi.FileName, psi.Arguments);
                process.Start();

                List<string> stdoutLines = new List<string>();
                if (!psi.UseShellExecute)
                {
                    process.OutputDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs eventArgs) =>
                    {
                        string? data = eventArgs?.Data;
                        if (!string.IsNullOrEmpty(data))
                        {
                            Console.WriteLine(data);
                            logFile.WriteLine(data);
                            stdoutLines.Add(data);
                        }
                    });
                    process.ErrorDataReceived += new DataReceivedEventHandler((object sender, DataReceivedEventArgs eventArgs) =>
                    {
                        string? data = eventArgs?.Data;
                        if (!string.IsNullOrEmpty(data))
                        {
                            Console.Error.WriteLine(data);
                            logFile.WriteLine("!!" + data);
                            stdoutLines.Add(data);
                        }
                    });

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                process.WaitForExit();

                logFile.WriteLine(
                    "Finished in {0} msecs with exit code {1}: {2} {3}",
                    sw.ElapsedMilliseconds,
                    process.ExitCode,
                    psi.FileName,
                    psi.Arguments);

                stdout = stdoutLines;

                return process.ExitCode;
            }
        }

        /*
        private static void ProcessXmlFile(string xmlFile, string resultsFile)
        {
            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.Load(xmlFile);

            StringBuilder details = new StringBuilder();
            details.AppendLine("Details");
            details.AppendLine("=======");

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("Summary");
            summary.AppendLine("=======");
            bool isBaseline = true;
            Dictionary<string, PhaseStatistics>? baselinePhaseStatistics = null;

            foreach (XmlNode buildAndRun in xmlDocument.GetElementsByTagName("BuildAndRun"))
            {
                string name = buildAndRun!.Attributes!["Name"]!.InnerText!;
                bool netCoreComposite = bool.Parse(buildAndRun!["NetCoreComposite"]!.InnerText!);
                bool netCoreIncludeAspNet = bool.Parse(buildAndRun!["NetCoreIncludeAspNet"]!.InnerText!);
                bool aspNetComposite = bool.Parse(buildAndRun!["AspNetComposite"]!.InnerText!);
                bool appR2R = bool.Parse(buildAndRun!["AppR2R"]!.InnerText!);
                bool appComposite = bool.Parse(buildAndRun!["AppComposite"]!.InnerText!);
                bool oneBigComposite = bool.Parse(buildAndRun!["OneBigComposite"]!.InnerText!);
                bool appAvx2 = bool.Parse(buildAndRun!["AppAVX2"]!.InnerText!);
                bool useTieredCompilation = bool.Parse(buildAndRun!["UseTieredCompilation"]!.InnerText!);
                bool useReadyToRun = bool.Parse(buildAndRun!["UseReadyToRun"]!.InnerText!);

                Dictionary<string, PhaseStatistics> phaseStatistics = new Dictionary<string, PhaseStatistics>();
                List<string> phaseOrdering = new List<string>();

                foreach (XmlNode result in buildAndRun!["Results"]!.ChildNodes!)
                {
                    string phase = result.Attributes!["Phase"]!.InnerText!;
                    int totalMsecs = int.Parse(result!["TotalTimeMsec"]!.InnerText!);
                    int userMsecs = int.Parse(result!["UserTimeMsec"]!.InnerText!);
                    int systemMsecs = int.Parse(result!["SystemTimeMsec"]!.InnerText!);

                    if (!phaseStatistics.TryGetValue(phase, out PhaseStatistics? statistics))
                    {
                        statistics = new PhaseStatistics();
                        phaseStatistics.Add(phase, statistics);
                        phaseOrdering.Add(phase);
                        if (isBaseline)
                        {
                            summary.AppendFormat("{0,-7} |  %  | ", phase);
                        }
                    }
                    statistics.Add(totalMsecs, userMsecs, systemMsecs);
                }

                if (isBaseline)
                {
                    summary.AppendLine("TOTAL   |  %  | MODE");
                    summary.AppendLine(new string('=', 16 * phaseOrdering.Count + 20));
                    baselinePhaseStatistics = phaseStatistics;
                }

                StringBuilder buildModeName = new StringBuilder();
                buildModeName.Append(name);
                buildModeName.Append(": ");
                if (oneBigComposite)
                {
                    buildModeName.Append("one big composite");
                }
                else
                {
                    buildModeName.AppendFormat(".NET Core{0}={1}",
                        netCoreIncludeAspNet ? "+ASP.NET" : "",
                        netCoreComposite ? "composite" : "default");
                    if (!netCoreIncludeAspNet)
                    {
                        buildModeName.AppendFormat(" / ASP.NET={0}", aspNetComposite ? "composite" : "default");
                    }
                    buildModeName.AppendFormat(" / APP={0}", !appR2R ? "JIT" : !appComposite ? "R2R" : "composite");
                }
                if (appAvx2)
                {
                    buildModeName.Append(" / AVX2");
                }
                buildModeName.AppendFormat(" / TC {0}", useTieredCompilation ? "ON" : "OFF");
                buildModeName.AppendFormat(" / RTR {0}", useReadyToRun ? "ON" : "OFF");

                details.AppendLine(buildModeName.ToString());
                details.AppendLine(new string('=', buildModeName.Length));
                int prevAverage = 0;
                int prevBaselineAverage = 0;
                for (int phaseIndex = 0; phaseIndex < phaseOrdering.Count; phaseIndex++)
                {
                    string phase = phaseOrdering[phaseIndex];
                    PhaseStatistics stat = phaseStatistics[phase];
                    stat.WriteTo(details, phase);
                    int average = stat.Total.Average;
                    int delta = Math.Max(average - prevAverage, 1);
                    PhaseStatistics baselineStat = baselinePhaseStatistics![phase];
                    int baselineAverage = baselineStat.Total.Average;
                    int baselineDelta = Math.Max(baselineAverage - prevBaselineAverage, 1);
                    prevAverage = average;
                    prevBaselineAverage = baselineAverage;
                    summary.AppendFormat("{0,7} | {1,3} | ", average, Percentage(delta, baselineDelta));
                }
                string lastPhase = phaseOrdering[phaseOrdering.Count - 1];
                PhaseStatistics lastStat = phaseStatistics[lastPhase];
                PhaseStatistics lastBaseline = baselinePhaseStatistics![lastPhase];
                summary.AppendFormat("{0,7} | {1,3} | ", lastStat.Total.Average, Percentage(lastStat.Total.Average, lastBaseline.Total.Average));
                details.AppendLine();
                summary.AppendLine(name);

                isBaseline = false;
            }

            string results = details.ToString() + Environment.NewLine + summary.ToString();

            Console.Write(results);
            File.WriteAllText(resultsFile, results);
        }
        */

        private static int Percentage(int numerator, int denominator)
        {
            return (int)(numerator * 100.0 / Math.Max(denominator, 1));
        }
    }
}

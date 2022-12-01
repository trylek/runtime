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
    public struct PublishInfo
    {
        public int Size;
        public int TotalFiles;
        public int CompositeFiles;
        public int CompositeSize;
        public int SingleFiles;
        public int SingleSize;
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
        private const int WarmupIterations = 2;
        private const int Iterations = 50;

        private static bool UseLinux;
        private static bool UseReadyToRun = true;
        private static bool UseTieredCompilation;
        private static bool UseContainers = true;
        private static bool UsePartialComposite;
        private static bool UseFastMode;

        private static bool NextArgIsCompositeFileList;

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
            foreach (string arg in args)
            {
                if (NextArgIsCompositeFileList)
                {
                    s_compositeFileList = arg;
                    NextArgIsCompositeFileList = false;
                    continue;
                }

                switch (arg)
                {
                    case "LINUX":
                        UseLinux = true;
                        break;

                    case "WINDOWS":
                        UseLinux = false;
                        break;

                    case "NORTR":
                        UseReadyToRun = false;
                        break;

                    case "TIER":
                        UseTieredCompilation = true;
                        break;

                    case "NOCONT":
                        UseContainers = false;
                        break;

                    case "PARTIAL":
                        UsePartialComposite = true;
                        NextArgIsCompositeFileList = true;
                        break;

                    case "FAST":
                        UseFastMode = true;
                        break;

                    default:
                        throw new Exception($"Unsupported argument '{arg}'");
                }
            }

            string rid = (UseLinux ? "linux" : "win") + "-x64";

            s_timestamp = DateTime.Now.ToString("MMdd-HHmm");
            s_folderName = Directory.GetCurrentDirectory();
            s_publishFolderName = Path.Combine(s_folderName, "bin", "Release", "net7.0", rid, "publish", "app");

            string buildLogFile = Path.Combine(s_folderName, $"build-{s_timestamp}.log");
            string execLogFile = Path.Combine(s_folderName, $"run-{s_timestamp}.log");
            string resultsCsvFile = Path.Combine(s_folderName, $"results-{s_timestamp}.csv");

            Statistics[] results = new Statistics[s_buildModes.Length];
            int[] jitMethodCounts = new int[s_buildModes.Length];

            using (StreamWriter buildLogWriter = new StreamWriter(buildLogFile))
            using (StreamWriter execLogWriter = new StreamWriter(execLogFile))
            using (StreamWriter resultsCsvWriter = new StreamWriter(resultsCsvFile))
            {
                s_buildLogFile = buildLogWriter;
                s_execLogFile = execLogWriter;
                s_resultsCsvFile = resultsCsvWriter;
                if (UsePartialComposite)
                {
                    MeasurePartialComposite();
                }
                else
                {
                    for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                    {
                        BuildAndRun(s_buildModes[modeIndex], modeIndex, s_buildModes.Length,
                            compositeFileList: "", compositeFileCount: 0, out results[modeIndex], out jitMethodCounts[modeIndex],
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
                            jitMethodCounts[modeIndex],
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

            const string buildMode = "default-r2r";
            BuildAndRun(buildMode, 0, 1, "", 0, out Statistics firstPartialStat, out int firstPartialMethodCount, out PublishInfo firstPublishInfo);
            int totalFiles = firstPublishInfo.TotalFiles;
            Statistics[] statistics = new Statistics[totalFiles + 1];
            int[] jitMethodCount = new int[totalFiles + 1];
            PublishInfo[] publishInfos = new PublishInfo[totalFiles + 1];
            bool[] calculated = new bool[totalFiles + 1];

            statistics[0] = firstPartialStat;
            jitMethodCount[0] = firstPartialMethodCount;
            publishInfos[0] = firstPublishInfo;
            calculated[0] = true;

            // Build full composite
            BuildAndRun(buildMode, totalFiles, totalFiles,
                compositeFileList: "*", compositeFileCount: 1, out Statistics fullStat, out int fullMethodCount, out PublishInfo fullPublishInfo);
            statistics[totalFiles] = fullStat;
            jitMethodCount[totalFiles] = fullMethodCount;
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
            if (UseFastMode && high - low <= 20)
            {
                bisect = false;
            }

            if (bisect)
            {
                int middle = (high + low) >> 1;
                BuildAndRun(buildMode, middle, totalFiles,
                    compositeFileList, middle,
                    out Statistics middleStat, out int middleMethodCount, out PublishInfo middlePublishInfo);
                statistics[middle] = middleStat;
                jitMethodCount[middle] = middleMethodCount;
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
            string compositeFileList, int compositeFileCount, out Statistics stat, out int jitMethodCount,
            out PublishInfo publishInfo)
        {
            string? image = Build(buildMode, UseContainers, index, count,
                compositeFileList, compositeFileCount, out publishInfo);
            if (image == null)
            {
                stat = new Statistics();
                jitMethodCount = 0;
                return;
            }
            Run(buildMode, image, useTieredCompilation: UseTieredCompilation, useReadyToRun: UseReadyToRun, out stat, out jitMethodCount);
        }

        private static string? Build(in string buildMode, bool useContainers, int index, int total,
            in string compositeFileList, int compositeFileCount, out PublishInfo publishInfo)
        {
            Stopwatch sw = Stopwatch.StartNew();
            s_buildLogFile!.WriteLine("Building configuration: {0} ({1} / {2})", buildMode, index, total);

            StringBuilder buildArgs = new StringBuilder();
            buildArgs.Append(UseLinux ? "linux" : "win");
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
            publishInfo = default;

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

        private static void TryReadCG2Value(string line, string tag, ref int value)
        {
            int tagIndex = line.IndexOf(tag);
            if (tagIndex >= 0)
            {
                int start = tagIndex + tag.Length;
                int end = start;
                while (end < line.Length && char.IsDigit(line[end]))
                {
                    end++;
                }
                value = int.Parse(line.AsSpan(start, end - start));
            }
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
                environment.Add(new KeyValuePair<string, string>("TotalIterations", (WarmupIterations + Iterations).ToString()));
            }
            environment.Add(new KeyValuePair<string, string>("DOTNET_ReadyToRun", UseReadyToRun ? "1" : "0"));

            string runScriptName = "runapp." + (UseLinux ? "sh" : "cmd");
            string application;
            StringBuilder commandLine = new StringBuilder();
            if (UseContainers)
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

                if (UseLinux)
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
            if (!UseContainers)
            {
                foreach (KeyValuePair<string, string> kvpEnv in environment)
                {
                    psi.Environment[kvpEnv.Key] = kvpEnv.Value;
                }
            }
            return RunProcess(psi, logFile, out stdout);
        }

        private static void Run(string buildMode, string dockerImageId, bool useTieredCompilation, bool useReadyToRun,
            out Statistics stat, out int jitMethodCount)
        {
            s_execLogFile!.WriteLine("Running configuration: {0}", buildMode);

            jitMethodCount = 0;
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
                int jitIndex = line.IndexOf(": JIT compiled");
                if (jitIndex > 0)
                {
                    while (jitIndex > 0 && line[jitIndex - 1] < ' ')
                    {
                        jitIndex--;
                    }
                    int numberEnd = jitIndex;
                    while (jitIndex > 0 && char.IsDigit(line[jitIndex - 1]))
                    {
                        jitIndex--;
                    }
                    int numberBegin = jitIndex;
                    if (int.TryParse(line.AsSpan(numberBegin, numberEnd - numberBegin), out int methodIndex))
                    {
                        jitMethodCount = Math.Max(jitMethodCount, methodIndex);
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
            s_execLogFile!.WriteLine("JITTED:  {0}", jitMethodCount);
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

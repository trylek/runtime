// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ContPerf
{
    public sealed class Program
    {
        private const string JitCompiledTag = ": JIT compiled";
        private const int WarmupIterations = 2;
        private const int DefaultIterations = 2;
        private const int CrankRetryAttempts = 3;
        private const int CrankRetryDelayMilliseconds = 30000;
        private const int MinFastCountToBisect = 10;

        private static bool s_useLinux;
        private static bool s_useCrank;
        private static bool s_logJitSummary;
        private static bool s_useReadyToRun = true;
        private static bool s_useTieredCompilation;
        private static bool s_useContainers = true;
        private static bool s_usePartialComposite;
        private static bool s_measureNegativeComposite;
        private static bool s_useCrossModuleInlining;
        private static bool s_useFastMode;
        private static bool s_buildFullComposite;
        private static bool s_emitMapFile;
        private static bool s_useHotColdSplitting;
        private static int? s_partialIndex;
        private static int s_iterations = DefaultIterations;

        private enum NextArg
        {
            Crossgen2Path,
            Command,
            CompositeFileList,
            PartialIndex,
            Iterations,
            CrankConfigFile,
            CrankScenario,
            CrankApp,
            AppName,
            CsvOutputFile,
            MibcFile,
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

        private static string s_crossgen2Path = "";
        private static string s_publishFolderName = "";
        private static string s_appFolderName = "";
        private static string s_logsFolderName = "";
        private static string s_crankConfigFile = "";
        private static string s_crankScenario = "";
        private static string s_crankApp = "";
        private static string s_appName = "";
        private static string s_csvOutputFile = "";
        private static string s_mibcFile = "";

        private static string? s_timestamp;

        private static TextWriter? s_buildLogFile;
        private static TextWriter? s_execLogFile;
        private static TextWriter? s_resultsCsvFile;

        private static Dictionary<int, CsvInfo> s_csvCache = new Dictionary<int, CsvInfo>();

        private static Stopwatch s_stopwatch = Stopwatch.StartNew();

        public static int Main(string[] args)
        {
            NextArg nextArg = NextArg.Crossgen2Path;

            foreach (string arg in args)
            {
                switch (nextArg)
                {
                    case NextArg.Crossgen2Path:
                        s_crossgen2Path = arg;
                        nextArg = NextArg.Command;
                        break;

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

                    case NextArg.CrankConfigFile:
                        s_crankConfigFile = arg;
                        nextArg = NextArg.CrankScenario;
                        break;

                    case NextArg.CrankScenario:
                        s_crankScenario = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.CrankApp:
                        s_crankApp = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.AppName:
                        s_appName = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.CsvOutputFile:
                        s_csvOutputFile = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.MibcFile:
                        s_mibcFile = arg;
                        nextArg = NextArg.Command;
                        break;

                    case NextArg.Command:
                        switch (arg)
                        {
                            case "CMI":
                                s_useCrossModuleInlining = true;
                                break;

                            case "CRANK":
                                s_useCrank = true;
                                nextArg = NextArg.CrankConfigFile;
                                break;

                            case "CRANKAPP":
                                nextArg = NextArg.CrankApp;
                                break;

                            case "CSV":
                                nextArg = NextArg.CsvOutputFile;
                                break;

                            case "FULL":
                                s_buildFullComposite = true;
                                break;

                            case "MAP":
                                s_emitMapFile = true;
                                break;

                            case "LINUX":
                                s_useLinux = true;
                                break;

                            case "WINDOWS":
                                s_useLinux = false;
                                break;

                            case "JIT":
                                s_logJitSummary = true;
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

                            case "APP":
                                nextArg = NextArg.AppName;
                                break;

                            case "MIBC":
                                nextArg = NextArg.MibcFile;
                                break;

                            case "HOTCOLDSPLITTING":
                                s_useHotColdSplitting = true;
                                break;

                            default:
                                throw new Exception($"Unsupported argument '{arg}'");
                        }
                        break;

                    default:
                        throw new NotImplementedException(nextArg.ToString());
                }
            }

            // string rid = (s_useLinux ? "linux" : "win") + "-x64";

            s_timestamp = DateTime.Now.ToString("MMdd-HHmm");
            s_publishFolderName = Directory.GetCurrentDirectory();
            s_appFolderName = Path.Combine(s_publishFolderName, "app");
            s_logsFolderName = Path.Combine(s_publishFolderName, "logs");

            if (!string.IsNullOrEmpty(s_csvOutputFile) && File.Exists(s_csvOutputFile))
            {
                s_csvCache = CsvInfo.ParseCsvFile(s_csvOutputFile);
            }

            Directory.CreateDirectory(s_logsFolderName);

            string buildLogFile = Path.Combine(s_logsFolderName, $"build-{s_timestamp}.log");
            string execLogFile = Path.Combine(s_logsFolderName, $"run-{s_timestamp}.log");
            string resultsCsvFile = Path.Combine(s_logsFolderName, $"results-{s_timestamp}.csv");

            ExecutionInfo[] results = new ExecutionInfo[s_buildModes.Length];

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
                        BuildAndRun(modeIndex, s_buildModes.Length,
                            compositeFileList: "", compositeFileCount: 0,
                            out results[modeIndex],
                            out PublishInfo publishInfo);
                    }

                    s_execLogFile.WriteLine("   COUNT |     AVG% |      AVG |      JIT | MODE");
                    s_execLogFile.WriteLine("------------------------------------------------");
                    for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                    {
                        ExecutionInfo result = results[modeIndex];
                        long averagePercentage = (result.StartTimeUsecs.Average * 100L / Math.Max(results[0].StartTimeUsecs.Average, 1));
                        s_execLogFile.WriteLine("{0,8} | {1,8} | {2,8} | {3,8} | {4}",
                            result.StartTimeUsecs.Count,
                            averagePercentage,
                            result.StartTimeUsecs.Average,
                            result.JittedMethods.Count,
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

            if (!string.IsNullOrEmpty(s_csvOutputFile))
            {
                Console.WriteLine("Updating CSV file: {0}", s_csvOutputFile);
                File.Copy(resultsCsvFile, s_csvOutputFile, overwrite: true);
            }

            return 0;
        }

        private static void MeasurePartialComposite()
        {
            if (s_partialIndex.HasValue)
            {
                BuildPartialCompositeAtIndex(s_compositeFileList!, s_partialIndex.Value);
                return;
            }

            if (s_measureNegativeComposite)
            {
                MeasureNegativeComposite();
                return;
            }

            BuildAndRun(0, 1, "", 0, out ExecutionInfo firstExecInfo, out PublishInfo firstPublishInfo);
            int totalFiles = firstPublishInfo.TotalFiles;
            ExecutionInfo[] statistics = new ExecutionInfo[totalFiles + 1];
            PublishInfo[] publishInfos = new PublishInfo[totalFiles + 1];
            bool[] calculated = new bool[totalFiles + 1];

            statistics[0] = firstExecInfo;
            publishInfos[0] = firstPublishInfo;
            calculated[0] = true;


            // Build full composite
            BuildAndRun(totalFiles, totalFiles,
                compositeFileList: s_compositeFileList!, compositeFileCount: totalFiles,
                out ExecutionInfo fullExecInfo, out PublishInfo fullPublishInfo);
            statistics[totalFiles] = fullExecInfo;
            publishInfos[totalFiles] = fullPublishInfo;
            calculated[totalFiles] = true;

            try
            {
                BisectPartialComposite(totalFiles, s_compositeFileList!, statistics, publishInfos, calculated, 0, totalFiles);
            }
            catch(Exception ex)
            {
                Console.WriteLine("Error: {0}", ex);
            }

            s_execLogFile!.WriteLine("#TOTAL    | #COMPOSITE | PUBLISH SIZE | COMP-SIZE | SINGLE-SIZE | JIT COUNT | STARTUP USECS | WORKING SET | PRIVATE MEMORY");
            s_execLogFile!.WriteLine("--------------------------------------------------------------------------------------------------------------------------");
            s_resultsCsvFile!.WriteLine("TOTAL,COMPOSITE,PUBLISH_SIZE,COMP_SIZE,SINGLE_SIZE,JIT_COUNT,STARTUP_USECS,WORKING_SET_MB,PRIVATE_MEMORY_MB");
            for (int index = 0; index <= totalFiles; index++)
            {
                PublishInfo info = publishInfos[index];
                ExecutionInfo stat = statistics[index];
                if (stat != null)
                {
                    s_execLogFile!.WriteLine(
                        "{0,9} | {1,10} | {2,12} | {3,9} | {4,11} | {5,9} | {6,13} | {7,11} | {8,14}",
                        info.TotalFiles,
                        index,
                        info.TotalSize,
                        info.CompositeSize,
                        info.SingleSize,
                        stat.JittedMethods.Count,
                        stat.StartTimeUsecs.Minimum,
                        stat.WorkingSetMB.Maximum,
                        stat.PrivateMemoryMB.Maximum);

                    s_resultsCsvFile!.WriteLine(
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                        info.TotalFiles,
                        index,
                        info.TotalSize,
                        info.CompositeSize,
                        info.SingleSize,
                        stat.JittedMethods.Count,
                        stat.StartTimeUsecs.Minimum,
                        stat.WorkingSetMB.Maximum,
                        stat.PrivateMemoryMB.Maximum);
                }
            }
        }

        private static void MeasureNegativeComposite()
        {
            // Build full composite
            BuildAndRun(1, 1,
                compositeFileList: s_compositeFileList!, compositeFileCount: int.MaxValue,
                out ExecutionInfo fullStat,
                out PublishInfo fullPublishInfo);
            int totalFiles = fullPublishInfo.CompositeFiles;
            if (s_useFastMode)
            {
                totalFiles = Math.Min(totalFiles, 10);
            }
            PublishInfo[] publishInfo = new PublishInfo[totalFiles + 1];
            ExecutionInfo[] statistics = new ExecutionInfo[totalFiles + 1];

            publishInfo[0] = fullPublishInfo;
            statistics[0] = fullStat;

            for (int i = 0; i < totalFiles; i++)
            {
                BuildAndRun(i, totalFiles,
                    s_compositeFileList!, compositeFileCount: ~i,
                    out statistics[i + 1], out publishInfo[i + 1]);
                ShowProgress(i + 1, totalFiles);
            }

            s_resultsCsvFile!.WriteLine("INDEX,PUBLISH_SIZE,STARTUP_USECS,JIT_COUNT,SIZE_DELTA,STARTUP_DELTA_USECS,JIT_DELTA,EXCLUDED_ASSEMBLY,");
            for (int i = 0; i <= totalFiles; i++)
            {
                PublishInfo info = publishInfo[i];
                ExecutionInfo stat = statistics[i];
                s_resultsCsvFile!.WriteLine("{0},{1:F6},{2:F6},{3},{4:F6},{5:F6},{6},{7},",
                    i,
                    info.TotalSize,
                    stat.StartTimeUsecs.Minimum,
                    stat.JittedMethods.Count,
                    (info.TotalSize - fullPublishInfo.TotalSize),
                    (stat.StartTimeUsecs.Minimum - fullStat.StartTimeUsecs.Minimum),
                    stat.JittedMethods.Count - fullStat.JittedMethods.Count,
                    info.SingleAssemblies.FirstOrDefault() ?? "(none)");
            }
        }


        private static void BuildPartialCompositeAtIndex(string compositeFileList, int compositeFileIndex)
        {
            BuildAndRun(1, 1,
                compositeFileList, compositeFileIndex,
                out ExecutionInfo stat, out PublishInfo publishInfo);

            s_buildLogFile!.WriteLine("Startup time AVG:    {0}", stat.StartTimeUsecs.Average);
            s_buildLogFile!.WriteLine("Startup time MIN:    {0}", stat.StartTimeUsecs.Minimum);
            s_buildLogFile!.WriteLine("Startup time MAX:    {0}", stat.StartTimeUsecs.Maximum);
            s_buildLogFile!.WriteLine("Startup time STDDEV: {0}", stat.StartTimeUsecs.StandardDeviation);
            s_buildLogFile!.WriteLine("Runtime JIT count:   {0}", stat.JittedMethods.Count);
            s_buildLogFile!.WriteLine("Precise JIT count:   {0}", stat.JittedMethods.Count(m => m != ""));

            Dictionary<string, int> methodCounts = new Dictionary<string, int>();
            foreach (string method in stat.JittedMethods.Where(m => m != ""))
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
            ShowProgress(done, calculated.Length);
        }

        private static void ShowProgress(int done, int total)
        {
            if (done > s_lastProgress)
            {
                s_lastProgress = done;
                Console.WriteLine(s_separator);
                long eta = s_stopwatch.ElapsedMilliseconds * total / Math.Max(done, 1);
                Console.WriteLine("{0:F3} .. {1} / {2} ({3:F1}%, ETA {4:F3})",
                    s_stopwatch.ElapsedMilliseconds * 1e-3,
                    done,
                    total,
                    done * 100.0 / total,
                    eta * 1e-3);
                Console.WriteLine(s_separator);
            }
        }

        private static void BisectPartialComposite(
            int totalFiles, string compositeFileList,
            ExecutionInfo[] statistics,
            PublishInfo[] publishInfo,
            bool[] calculated,
            int low,
            int high)
        {
            ShowProgress(calculated);

            if (high <= low + 1)
            {
                return;
            }

            ExecutionInfo lowStat = statistics[low];
            ExecutionInfo highStat = statistics[high];
            PublishInfo lowPublish = publishInfo[low];
            PublishInfo highPublish = publishInfo[high];

            bool bisect = false;
            if (Math.Abs(highStat.StartTimeUsecs.Average - lowStat.StartTimeUsecs.Average) >= 10000)
            {
                bisect = true;
            }
            if (Math.Abs(highPublish.TotalSize - lowPublish.TotalSize) >= 500000)
            {
                bisect = true;
            }
            if (s_useFastMode && high - low <= MinFastCountToBisect)
            {
                bisect = false;
            }

            if (bisect)
            {
                int middle = (high + low) >> 1;
                BuildAndRun(middle, totalFiles, compositeFileList, middle, out ExecutionInfo middleExecInfo, out PublishInfo middlePublishInfo);
                statistics[middle] = middleExecInfo;
                publishInfo[middle] = middlePublishInfo;
                calculated[middle] = true;

                BisectPartialComposite(totalFiles, compositeFileList, statistics, publishInfo, calculated, low, middle);
                BisectPartialComposite(totalFiles, compositeFileList, statistics, publishInfo, calculated, middle, high);
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

        private static void BuildAndRun(int index, int count,
            string compositeFileList, int compositeFileCount, out ExecutionInfo stat,
            out PublishInfo publishInfo)
        {
            if (s_csvCache.TryGetValue(compositeFileCount, out CsvInfo? csvInfo))
            {
                publishInfo = csvInfo.Publish;
                stat = csvInfo.Stat;
                return;
            }

            string? image = Build(index, count,
                compositeFileList, compositeFileCount, out publishInfo);
            if (image == null || (s_iterations == 0 && !s_useCrank))
            {
                stat = new ExecutionInfo();
                return;
            }
            Run(image, useTieredCompilation: s_useTieredCompilation, useReadyToRun: s_useReadyToRun, out stat);
        }

        /*
        private static void BuildAndRunUsingCrankBenchmarker(in string buildMode, int index, int count,
            string compositeFileList, int compositeFileCount, out Statistics stat, out List<string> jitMethods,
            out PublishInfo publishInfo)
        {
            string configYamlFileName = Path.Combine(s_folderName, "config.yaml");
            string fxAssembliesFileName = Path.Combine(s_folderName, "fx.txt");
            string aspAssembliesFileName = Path.Combine(s_folderName, "asp.txt");

            StringBuilder configYaml = new StringBuilder();

            configYaml.AppendLine("assemblies:");

            configYaml.AppendLine("  windows: # Going to build the composites on a Windows machine, hence we need a Windows crossgen2 build.");
            configYaml.AppendLine("    crossgen2s:");
            configYaml.AppendLine("      - name: RuntimeRepo");
            if (string.IsNullOrEmpty(s_crossgen2Path))
            {
                throw new Exception("Crossgen2 path required for crank testing");
            }
            configYaml.AppendLine("        path: " + s_crossgen2Path.Replace("\\", "\\\\"));
            configYaml.AppendLine("");
            configYaml.AppendLine("configurations:");
            configYaml.AppendLine("  - name: " + (s_useLinux ? "LinuxOnWindows" : "WindowsOnWindows"));
            configYaml.AppendLine("    os: " + (s_useLinux ? "linux" : "windows"));
            configYaml.AppendLine("    assembliesToUse:");
            configYaml.AppendLine("      runtime: Latest # We'll be using a nightly build of the SDK.");
            configYaml.AppendLine("      crossgen2: RuntimeRepo # This is the key that points to which Crossgen2 you want to use. Note that the name above is 'RuntimeRepo'.");
            configYaml.AppendLine("    scenariosFile: https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/json.benchmarks.yml # Crank stuff.");
            configYaml.AppendLine("    scenario: json # We want to run the 'json' scenario defined in the file linked above.");
            configYaml.AppendLine("    buildPhase:");
            configYaml.AppendLine("      compositeFileList: " + compositeFileList.Replace("\\", "\\\\"));
            configYaml.AppendLine("      compositeFileCount: " + compositeFileCount.ToString());
            configYaml.AppendLine("      params:");
            if (buildMode == "runtime.composite" ||
                buildMode == "runtime+asp.net.composite" ||
                buildMode == "runtime.composite+asp.net.composite" ||
                buildMode == "full.composite")
            {
            configYaml.AppendLine("        - frameworkcomposite # Build framework composites.");
            }
            if (buildMode == "runtime+asp.net.composite" ||
                buildMode == "full.composite")
            {
            configYaml.AppendLine("        - bundleaspnet # Bundle/include the asp.net binaries into the composite image.");
            }
            configYaml.AppendLine("    runPhase:");
            configYaml.AppendLine("      params:");
            configYaml.AppendLine("        - appr2r # Tell crank to build its app using ReadyToRun enabled.");
            if (s_useReadyToRun)
            {
            configYaml.AppendLine("        - envreadytorun # Tell crank to set DOTNET_ReadyToRun=1 in its environment.");
            }
            if (s_useTieredCompilation)
            {
            configYaml.AppendLine("        - envtieredcompilation # Tell crank to set DOTNET_TieredCompilation=1 in its environment.");
            }

            File.WriteAllText(configYamlFileName, configYaml.ToString());

            StringBuilder args = new StringBuilder();

            args.Append(" --config-file ");
            args.Append(configYamlFileName);
            args.AppendFormat(" --iterations {0}", s_iterations);

            ProcessStartInfo benchmarkerPsi = new ProcessStartInfo()
            {
                FileName = s_crankBenchmarkerPath,
                Arguments = args.ToString(),
            };

            Console.WriteLine("Running crank ({0} / {1}): {2} {3}", index, count, s_crankBenchmarkerPath, args.ToString());
            Console.WriteLine("Config file:        {0}", configYamlFileName);
            Console.WriteLine("FX assemblies:      {0}", fxAssembliesFileName);
            Console.WriteLine("ASP.NET assemblies: {0}", aspAssembliesFileName);
            RunProcess(benchmarkerPsi, s_buildLogFile!, out List<string> _ / *stdout* /);
            // TODO: analyze results
            publishInfo = new PublishInfo();
            stat = new Statistics();
            jitMethods = new List<string>();
        }
    */

        private static string? Build(int index, int total,
            in string compositeFileList, int compositeFileCount, out PublishInfo publishInfo)
        {
            s_buildLogFile!.WriteLine("Building ({0} / {1})", index, total);

            new BuildEngine(
                s_crossgen2Path,
                useLinux: s_useLinux,
                useContainers: s_useContainers,
                buildFullComposite: s_buildFullComposite,
                emitMapFile: s_emitMapFile,
                useCrossModuleInlining: s_useCrossModuleInlining,
                useHotColdSplitting: s_useHotColdSplitting,
                s_publishFolderName,
                s_appFolderName,
                compositeFileList,
                compositeFileCount,
                s_buildLogFile,
                s_mibcFile)
                .Build(out publishInfo);

            s_buildLogFile!.WriteLine("Composite file list: {0}", compositeFileList);
            s_buildLogFile!.WriteLine("Composite index:     {0}", compositeFileCount);
            s_buildLogFile!.WriteLine("Publish size:        {0}", publishInfo.TotalSize);
            s_buildLogFile!.WriteLine("Composite size:      {0}", publishInfo.CompositeSize);
            s_buildLogFile!.WriteLine("Single size:         {0}", publishInfo.SingleSize);
            s_buildLogFile!.WriteLine("Total files:         {0}", publishInfo.TotalFiles);
            s_buildLogFile!.WriteLine("Composite files:     {0}", publishInfo.CompositeFiles);
            s_buildLogFile!.WriteLine("Single files:        {0}", publishInfo.SingleFiles);

            return "";
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

        private static void Run(string dockerImageId, bool useTieredCompilation, bool useReadyToRun, out ExecutionInfo stat)
        {
            stat = new ExecutionInfo();

            if (s_useCrank)
            {
                s_execLogFile!.WriteLine("Running crank ...");

                StringBuilder crankArgs = new StringBuilder();
                crankArgs.AppendFormat(" --config {0}", s_crankConfigFile);
                crankArgs.AppendFormat(" --application.source.project \"\"");
                crankArgs.AppendFormat(" --application.source.repository \"\"");
                crankArgs.AppendFormat(" --application.source.localFolder " + s_appFolderName);
                crankArgs.AppendFormat(" --application.executable " + LocateExecutable());
                crankArgs.AppendFormat(" --scenario {0}", s_crankScenario);
                int executionCount;
                if (s_logJitSummary)
                {
                    crankArgs.AppendFormat(" --application.environmentVariables DOTNET_JitDisasmSummary=1");
                    crankArgs.AppendFormat(" --load.environmentVariables DOTNET_JitDisasmSummary=1");
                    executionCount = s_iterations;
                }
                else
                {
                    executionCount = 1;
                    if (s_iterations != 0)
                    {
                        crankArgs.AppendFormat(" --iterations {0}", s_iterations);
                    }
                    else
                    {
                        crankArgs.AppendFormat(" --iterations 1"); ;
                        crankArgs.AppendFormat(" --variable warmup=0");
                        crankArgs.AppendFormat(" --variable duration=0");
                    }
                }
                crankArgs.AppendFormat(" --profile aspnet-perf-{0}", s_useLinux ? "lin" : "win");
                crankArgs.AppendFormat(" --profile short");

                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    FileName = !string.IsNullOrEmpty(s_crankApp) ? s_crankApp : "crank",
                    Arguments = crankArgs.ToString(),
                };

                int successCount = 0;
                int failureCount = 0;
                int exitCode = 0;
                List<string> stdout = new List<string>();
                while (failureCount < CrankRetryAttempts && successCount < executionCount)
                {
                    Console.WriteLine("Running crank: {0} {1}", psi.FileName, psi.Arguments);
                    exitCode = RunProcess(psi, s_execLogFile, out stdout);
                    if (exitCode == 0)
                    {
                        successCount++;
                    }
                    else
                    {
                        failureCount++;
                        Console.WriteLine("Waiting for {0} milliseconds before retrying...", CrankRetryDelayMilliseconds);
                        Thread.Sleep(CrankRetryDelayMilliseconds);
                    }
                }
                if (exitCode != 0)
                {
                    throw new Exception($"Error running crank: {exitCode}");
                }

                foreach (string line in stdout)
                {
                    ExtractMetric(line, "| Start Time (ms)     | ", ref stat.StartTimeUsecs, 1000);
                    ExtractMetric(line, "| Working Set (MB)    | ", ref stat.WorkingSetMB);
                    ExtractMetric(line, "| Private Memory (MB) | ", ref stat.PrivateMemoryMB);
                }
            }
            else
            {
                s_execLogFile!.WriteLine("Running app...");

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

                            while (stat.JittedMethods.Count <= methodIndex)
                            {
                                stat.JittedMethods.Add("");
                            }

                            stat.JittedMethods[methodIndex] = line.Substring(methodPos, methodEnd - methodPos);
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
                stat.StartTimeUsecs.Add(usecDurations);
            }

            s_execLogFile!.WriteLine("JITTED METHOD COUNT:          {0}", stat.JittedMethods.Count);
            s_execLogFile!.WriteLine("ITERATION COUNT:              {0}", stat.StartTimeUsecs.Count);
            s_execLogFile!.WriteLine("STARTUP TIME AVERAGE (USECS): {0}", stat.StartTimeUsecs.Average);
            s_execLogFile!.WriteLine("STARTUP TIME MINIMUM (USECS): {0}", stat.StartTimeUsecs.Minimum);
            s_execLogFile!.WriteLine("STARTUP TIME MAXIMUM (USECS): {0}", stat.StartTimeUsecs.Maximum);
            s_execLogFile!.WriteLine("STARTUP TIME STDDEV (USECS):  {0}", stat.StartTimeUsecs.StandardDeviation);
            s_execLogFile!.WriteLine("WORKING SET (MB):             {0}", stat.WorkingSetMB.Average);
            s_execLogFile!.WriteLine("PRIVATE MEMORY (MB):          {0}", stat.PrivateMemoryMB.Average);
        }

        private static bool ExtractMetric(string line, string tag, ref Statistics stat, int scale = 1)
        {
            if (!line.StartsWith(tag))
            {
                return false;
            }
            int numberStart = tag.Length;
            int numberEnd = numberStart;
            while (numberEnd < line.Length && char.IsDigit(line[numberEnd]))
            {
                numberEnd++;
            }

            if (numberEnd > numberStart)
            {
                int metric = int.Parse(line.AsSpan(numberStart, numberEnd - numberStart));
                stat.Add(metric * scale);
                return true;
            }

            return false;
        }

        private static string LocateExecutable()
        {
            string executable = s_appName;
            if (string.IsNullOrEmpty(executable))
            {
                List<string> executables = new List<string>();
                foreach (string exeCandidate in Directory.EnumerateFiles(s_appFolderName, s_useLinux ? "*" : "*.exe"))
                {
                    string name = Path.GetFileName(exeCandidate);
                    if (Path.GetFileNameWithoutExtension(name) == "createdump")
                    {
                        continue;
                    }
                    if (s_useLinux && name.Contains('.'))
                    {
                        continue;
                    }
                    executables.Add(name);
                }
                if (executables.Count == 0)
                {
                    throw new Exception($"Test executable not found in app folder {s_appFolderName}");
                }
                if (executables.Count > 1)
                {
                    throw new Exception($"Multiple executables found in app folder {s_appFolderName}: {string.Join("; ", executables)}");
                }
                executable = executables[0];
            }
            return executable;
        }

        /*

            List<string> lines = new List<string>(File.ReadAllLines(configFile));
            bool doneRepository = false;
            bool doneArguments = false;
            bool doneProject = false;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                int startIndex = 0;
                while (startIndex < line.Length && char.IsWhiteSpace(line[startIndex]))
                {
                    startIndex++;
                }
                const string RepositoryTag = "repository: ";
                const string ArgumentsTag = "arguments: ";
                const string ProjectTag = "project: ";
                if (!doneRepository && line.Length >= startIndex + RepositoryTag.Length && RepositoryTag == line.Substring(startIndex, RepositoryTag.Length))
                {
                    lines[index] = string.Concat(line.AsSpan(0, startIndex), "localFolder: ", s_appFolderName.Replace("\\", "\\\\"));
                    doneRepository = true;
                }
                else if (!doneArguments && line.Length >= startIndex + ArgumentsTag.Length && ArgumentsTag == line.Substring(startIndex, ArgumentsTag.Length))
                {
                    lines.Insert(index, string.Concat(line.AsSpan(0, startIndex), "executable: " + Path.GetFileName(executable)));
                    index++;
                    doneArguments = true;
                }
                else if (!doneProject && line.Length >= startIndex + ProjectTag.Length && ProjectTag == line.Substring(startIndex, ProjectTag.Length))
                {
                    lines.RemoveAt(index);
                    doneProject = true;
                    index--;
                }
            }

            if (!doneRepository)
            {
                throw new Exception($"Repository key not found in crank config file {s_crankConfigFile}");
            }
            if (!doneArguments)
            {
                throw new Exception($"Arguments key not found in crank config file {s_crankConfigFile}");
            }
            if (!doneProject)
            {
                throw new Exception($"Project key not found in crank config file {s_crankConfigFile}");
            }

            string tempConfigFile = Path.ChangeExtension(Path.GetTempFileName(), ".yml");
            File.WriteAllLines(tempConfigFile, lines);
            Console.WriteLine("Rewritten config file saved as: {0}", tempConfigFile);

            return tempConfigFile;
        }
        */

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

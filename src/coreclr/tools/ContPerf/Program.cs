using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Xml;

namespace ContPerf
{
    class Statistics
    {
        private long _count = 0;
        private long _sum = 0;
        private long _sumSquared = 0;
        private long _minimum = long.MaxValue;
        private long _maximum = 0;

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

    class Program
    {
        const string LinuxImageString = "writing image sha256:";
        const string WindowsImageString = "Successfully built ";
        const int WarmupIterations = 2;
        const int Iterations = 50;

        static bool UseLinux = false;
        static bool UseReadyToRun = true;
        static bool UseTieredCompilation = false;
        static bool UseContainers = true;

        private static string[] s_buildModes =
        {
            "default-r2r",
            "runtime.composite",
            "runtime+asp.net.composite",
            "runtime.composite+asp.net.composite",
            "full.composite",
            "cross-module-inlining",
        };

        static string s_folderName = "";
        static string s_publishFolderName = "";

        static string? s_timestamp;

        static TextWriter? s_buildLogFile;
        static TextWriter? s_execLogFile;

        static int Main(string[] args)
        {
            foreach (string arg in args)
            {
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

                    default:
                        throw new Exception($"Unsupported argument '{arg}'");
                }
            }

            string rid = (UseLinux ? "linux" : "win") + "-x64";

            s_timestamp = DateTime.Now.ToString("MMdd-HHmm");
            s_folderName = Directory.GetCurrentDirectory();
            s_publishFolderName = Path.Combine(s_folderName, "bin", "Release", "net7.0", rid, "publish");

            string buildLogFile = Path.Combine(s_folderName, $"build-{s_timestamp}.log");
            string execLogFile = Path.Combine(s_folderName, $"run-{s_timestamp}.log");

            Statistics[] results = new Statistics[s_buildModes.Length];
            int[] jitMethodCounts = new int[s_buildModes.Length];

            using (StreamWriter buildLogWriter = new StreamWriter(buildLogFile))
            using (StreamWriter execLogWriter = new StreamWriter(execLogFile))
            {
                s_buildLogFile = buildLogWriter;
                s_execLogFile = execLogWriter;
                for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                {
                    BuildAndRun(s_buildModes[modeIndex], modeIndex, s_buildModes.Length, out results[modeIndex], out jitMethodCounts[modeIndex]);
                }
                s_buildLogFile = null;
                s_execLogFile = null;
            }

            Console.WriteLine("   COUNT |     AVG% |      AVG |      JIT | MODE");
            Console.WriteLine("------------------------------------------------");
            for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
            {
                Statistics result = results[modeIndex];
                long averagePercentage = (result.Average * 100L / Math.Max(results[0].Average, 1));
                Console.WriteLine("{0,8} | {1,8} | {2,8} | {3,8} | {4}",
                    result.Count,
                    averagePercentage,
                    result.Average,
                    jitMethodCounts[modeIndex],
                    s_buildModes[modeIndex]);
            }

            return 0;
        }

        private static void BuildAndRun(in string buildMode, int index, int count, out Statistics stat, out int jitMethodCount)
        {
            string? image = Build(buildMode, index, count);
            if (image == null)
            {
                stat = new Statistics();
                jitMethodCount = 0;
                return;
            }
            Run(buildMode, image, useTieredCompilation: UseTieredCompilation, useReadyToRun: UseReadyToRun, out stat, out jitMethodCount);
        }

        private static string? Build(in string buildMode, int index, int total)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Console.WriteLine("Building configuration: {0} ({1} / {2})", buildMode, index, total);

            StringBuilder buildArgs = new StringBuilder();
            buildArgs.Append(UseLinux ? "linux" : "win");
            buildArgs.Append(' ');
            buildArgs.Append(buildMode);

            ProcessStartInfo psiBuildCmd = new ProcessStartInfo()
            {
                FileName = Path.Combine(s_folderName!, "build.cmd"),
                Arguments = buildArgs.ToString(),
                UseShellExecute = false,
            };
            psiBuildCmd.Environment["UseContainers"] = UseContainers ? "1" : "0";

            int exitCode = RunProcess(psiBuildCmd, s_buildLogFile!, out List<string> stdout);

            string? imageId = null;
            if (exitCode == 0)
            {
                if (!UseContainers)
                {
                    return "";
                }

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
            Console.WriteLine("Done building configuration: {0} ({1} / {2}, {3} msecs)", buildMode, index, total, sw.ElapsedMilliseconds);
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
                environment.Add(new KeyValuePair<string, string>("TotalIterations", Iterations.ToString()));
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
                    commandLine.Append("=");
                    commandLine.Append(kvpEnv.Value);
                }

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
                    while (jitIndex > 0 && Char.IsDigit(line[jitIndex - 1]))
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
            for (int lineIndex = 0; lineIndex < stdout.Count; lineIndex++)
            {
                string line = stdout[lineIndex];
                const string USecsTag = "### USECS: ";
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
            Console.WriteLine("JITTED:  {0}", jitMethodCount);
            Console.WriteLine("COUNT:   {0}", stat.Count);
            Console.WriteLine("AVERAGE: {0}", stat.Average);
            Console.WriteLine("MINIMUM: {0}", stat.Minimum);
            Console.WriteLine("MAXIMUM: {0}", stat.Maximum);
            Console.WriteLine("STDDEV:  {0}", stat.StandardDeviation);
        }

        private static int RunProcess(ProcessStartInfo psi, TextWriter logFile, out List<string> stdout)
        {
            Console.WriteLine("RunProcess: {0} {1}", psi.FileName, psi.Arguments);

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

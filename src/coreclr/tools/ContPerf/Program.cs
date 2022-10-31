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
        const int Iterations = 10;

        static bool UseLinux = true;

        private static string[] s_buildModes =
        {
            "default-r2r",
            "runtime.composite",
            "runtime+asp.net.composite",
            "runtime.composite+asp.net.composite",
            "full.composite",
            "cross-module-inlining",
        };

        static string? s_folderName;

        static string? s_timestamp;

        static TextWriter? s_buildLogFile;
        static TextWriter? s_execLogFile;

        static int Main(string[] args)
        {
            s_timestamp = DateTime.Now.ToString("MMdd-HHmm");
            s_folderName = Directory.GetCurrentDirectory();

            string xmlFile;
            if (args.Length > 0)
            {
                xmlFile = args[0];
            }
            else
            {
                StringBuilder xml = new StringBuilder();
                xml.AppendLine("<Xml>");
                string buildLogFile = Path.Combine(s_folderName, $"build-{s_timestamp}.log");
                string execLogFile = Path.Combine(s_folderName, $"run-{s_timestamp}.log");

                Statistics[] results = new Statistics[s_buildModes.Length];

                using (StreamWriter buildLogWriter = new StreamWriter(buildLogFile))
                using (StreamWriter execLogWriter = new StreamWriter(execLogFile))
                {
                    s_buildLogFile = buildLogWriter;
                    s_execLogFile = execLogWriter;
                    for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                    {
                        results[modeIndex] = BuildAndRun(s_buildModes[modeIndex], xml, modeIndex, s_buildModes.Length);
                    }
                    s_buildLogFile = null;
                    s_execLogFile = null;
                }
                xml.AppendLine("</Xml>");
                //Console.WriteLine(new string('=', 70));
                //Console.WriteLine(xml.ToString());
                xmlFile = Path.Combine(s_folderName, $"results-{s_timestamp}.xml");
                File.WriteAllText(xmlFile, xml.ToString());

                Console.WriteLine("   COUNT |     AVG% |      AVG |      MIN |      MAX |   STDDEV | MODE");
                Console.WriteLine("----------------------------------------------------------------------");
                for (int modeIndex = 0; modeIndex < s_buildModes.Length; modeIndex++)
                {
                    Statistics result = results[modeIndex];
                    long averagePercentage = (result.Average * 100L / Math.Max(results[0].Average, 1));
                    Console.WriteLine("{0,8} | {1,8} | {2,8} | {3,8} | {4,8} | {5,8} | {6}",
                        result.Count,
                        averagePercentage,
                        result.Average,
                        result.Minimum,
                        result.Maximum,
                        result.StandardDeviation,
                        s_buildModes[modeIndex]);
                }
            }

            /*
            string resultsFile = Path.ChangeExtension(xmlFile, "results.txt");
            ProcessXmlFile(xmlFile, resultsFile);
            */
            return 0;
        }

        private static Statistics BuildAndRun(in string buildMode, StringBuilder xml, int index, int count)
        {
            string? image = Build(buildMode, index, count);
            if (image == null)
            {
                return new Statistics();
            }
            return Run(buildMode, image, xml, useTieredCompilation: false, useReadyToRun: true);
            /*
            xml.AppendFormat("<BuildAndRun Name=\"{0}\">\n", buildMode.Name);
            xml.AppendFormat("<NetCoreComposite>{0}</NetCoreComposite>\n", buildMode.NetCoreComposite);
            xml.AppendFormat("<NetCoreIncludeAspNet>{0}</NetCoreIncludeAspNet>\n", buildMode.NetCoreIncludeAspNet);
            xml.AppendFormat("<AspNetComposite>{0}</AspNetComposite>\n", buildMode.AspNetComposite);
            xml.AppendFormat("<AppR2R>{0}</AppR2R>\n", buildMode.AppR2R);
            xml.AppendFormat("<AppComposite>{0}</AppComposite>\n", buildMode.AppComposite);
            xml.AppendFormat("<OneBigComposite>{0}</OneBigComposite>\n", buildMode.OneBigComposite);
            xml.AppendFormat("<CrossModuleInlining>{0}</CrossModuleInlining>\n", buildMode.CrossModuleInlining);
            xml.AppendFormat("<AppAVX2>{0}</AppAVX2>\n", buildMode.AppAVX2);
            xml.AppendFormat("<UseTieredCompilation>{0}</UseTieredCompilation>\n", buildMode.UseTieredCompilation);
            xml.AppendFormat("<UseReadyToRun>{0}</UseReadyToRun>\n", buildMode.UseReadyToRun);
            xml.AppendLine("<Results>");
            StringBuilder warmupBuilder = new StringBuilder();
            for (int warmupIteration = 0; Iterations < WarmupIterations; warmupIteration++)
            {
                Run(buildMode, image, warmupBuilder);
            }
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                Run(buildMode, image, xml);
            }
            xml.AppendLine("</Results>");
            xml.AppendLine("</BuildAndRun>");
            */
        }

        private static string? Build(in string buildMode, int index, int total)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Console.WriteLine("Building configuration: {0} ({1} / {2})", buildMode, index, total);

            ProcessStartInfo psiBuildCmd = new ProcessStartInfo()
            {
                FileName = Path.Combine(s_folderName!, "build.cmd"),
                Arguments = (UseLinux ? "linux" : "win") + " " + buildMode,
                UseShellExecute = false,
            };
            /*
            psiBuildCmd.Environment["NETCORE_COMPOSITE"] = buildMode.NetCoreComposite ? "1" : "0";
            psiBuildCmd.Environment["NETCORE_INCLUDE_ASPNET"] = buildMode.NetCoreIncludeAspNet ? "1" : "0";
            psiBuildCmd.Environment["ASPNET_COMPOSITE"] = buildMode.AspNetComposite ? "1" : "0";
            psiBuildCmd.Environment["APP_R2R"] = buildMode.AppR2R ? "1" : "0";
            psiBuildCmd.Environment["APP_COMPOSITE"] = buildMode.AppComposite ? "1" : "0";
            psiBuildCmd.Environment["ONE_BIG_COMPOSITE"] = buildMode.OneBigComposite ? "1" : "0";
            psiBuildCmd.Environment["CROSS_MODULE_INLINING"] = buildMode.CrossModuleInlining ? "1" : "0";
            psiBuildCmd.Environment["APP_AVX2"] = buildMode.AppAVX2 ? "1" : "0";
            */

            int exitCode = RunProcess(psiBuildCmd, s_buildLogFile!, out List<string> stdout);

            string? imageId = null;
            if (exitCode == 0)
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

        private static Statistics Run(string buildMode, string dockerImageId, StringBuilder xml, bool useTieredCompilation, bool useReadyToRun)
        {
            StringBuilder commandLine = new StringBuilder();
            commandLine.Append("run");
            commandLine.AppendFormat(" --env COMPlus_TieredCompilation={0}", useTieredCompilation ? "1" : "0");
            // commandLine.AppendFormat(" --env COMPlus_ReadyToRun={0}", useReadyToRun ? "1" : "0");
            commandLine.AppendFormat(" -it {0}", dockerImageId);
            if (UseLinux)
            {
                commandLine.Append(" /app/runapp.sh");
            }
            else
            {
                commandLine.Append(" c:\\app\\runapp.cmd");
            }

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = "docker",
                Arguments = commandLine.ToString(),
                UseShellExecute = false,
            };
            // psi.EnvironmentVariables.Add("COMPlus_TieredCompilation", buildMode.UseTieredCompilation ? "1" : "0");
            // psi.EnvironmentVariables.Add("COMPlus_ReadyToRun", buildMode.UseReadyToRun ? "1" : "0");

            int exitCode = RunProcess(psi, s_execLogFile!, out List<string> stdout);
            if (exitCode != 0)
            {
                return new Statistics();
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
                            if (iteration > usecDurations.Count)
                            {
                                long duration = long.Parse(line.AsSpan(iterationEnd + 1, valueEnd - iterationEnd - 1));
                                usecDurations.Add(duration);
                            }
                        }
                    }
                }
            }
            Statistics stat = new Statistics(usecDurations);
            Console.WriteLine("COUNT:   {0}", stat.Count);
            Console.WriteLine("AVERAGE: {0}", stat.Average);
            Console.WriteLine("MINIMUM: {0}", stat.Minimum);
            Console.WriteLine("MAXIMUM: {0}", stat.Maximum);
            Console.WriteLine("STDDEV:  {0}", stat.StandardDeviation);
            return stat;
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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace ILTransform
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                string testRoot = "";
                bool deduplicateClassNames = false;
                bool deduplicateProjectNames = false;
                string classToDeduplicate = "";
                bool fixImplicitSharedLibraries = false;
                bool addILFactAttributes = false;
                bool addProcessIsolation = false;
                bool unifyDbgRelProjects = false;
                bool cleanupILModuleAssembly = false;
                foreach (string arg in args)
                {
                    if (arg[0] == '-')
                    {
                        if (arg.StartsWith("-d"))
                        {
                            deduplicateClassNames = true;
                            int index = 2;
                            while (index < arg.Length && !TestProject.IsIdentifier(arg[index]))
                            {
                                index++;
                            }
                            if (index < arg.Length)
                            {
                                classToDeduplicate = arg.Substring(index);
                            }
                        }
                        else if (arg.StartsWith("-i"))
                        {
                            fixImplicitSharedLibraries = true;
                        }
                        else if (arg.StartsWith("-f"))
                        {
                            addILFactAttributes = true;
                        }
                        else if (arg.StartsWith("-s"))
                        {
                            addProcessIsolation = true;
                        }
                        else if (arg.StartsWith("-p"))
                        {
                            unifyDbgRelProjects = true;
                        }
                        else if (arg.StartsWith("-m"))
                        {
                            cleanupILModuleAssembly = true;
                        }
                        else if (arg.StartsWith("-n"))
                        {
                            deduplicateProjectNames = true;
                        }
                        else
                        {
                            throw new Exception(string.Format("Unsupported option '{0}'", arg));
                        }
                    }
                    else
                    {
                        testRoot = arg;
                    }
                }


                if (testRoot == "")
                {
                    throw new Exception("Usage: ILTransform <test source root, e.g. d:\\git\\runtime\\src\\tests> [-d]");
                }

                string wrapperRoot = Path.Combine(testRoot, "generated", "wrappers");
                string logPath = Path.Combine(wrapperRoot, "wrapper.log");
                Directory.CreateDirectory(wrapperRoot);
                foreach (string file in Directory.GetFiles(wrapperRoot))
                {
                    File.Delete(file);
                }

                TestProjectStore testStore = new TestProjectStore();
                testStore.AddCommonClassName("My");
                testStore.ScanTree(testRoot);
                testStore.GenerateExternAliases();

                using (StreamWriter log = new StreamWriter(logPath))
                {
                    testStore.DumpFolderStatistics(log);
                    testStore.DumpDebugOptimizeStatistics(log);
                    testStore.DumpIrregularProjectSuffixes(log);
                    testStore.DumpImplicitSharedLibraries(log);
                    testStore.DumpMultiProjectSources(log);
                    testStore.DumpDuplicateProjectContent(log);
                    testStore.DumpDuplicateSimpleProjectNames(log);
                    testStore.DumpDuplicateEntrypointClasses(log);
                    testStore.DumpProjectsWithoutFactAttributes(log);
                    testStore.DumpCommandLineVariations(log);
                }

                if (fixImplicitSharedLibraries)
                {
                    // TODO
                }
                else if (unifyDbgRelProjects)
                {
                    testStore.UnifyDbgRelProjects();
                }
                else if (deduplicateProjectNames)
                {
                    testStore.DeduplicateProjectNames();
                }
                else
                {
                    testStore.RewriteAllTests(
                        deduplicateClassNames,
                        classToDeduplicate,
                        addProcessIsolation: addProcessIsolation,
                        addILFactAttributes: addILFactAttributes,
                        cleanupILModuleAssembly: cleanupILModuleAssembly);
                    if (!deduplicateClassNames && !addProcessIsolation && !addILFactAttributes)
                    {
                        testStore.GenerateAllWrappers(wrapperRoot);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal error: {0}", ex.ToString());
                return 1;
            }
        }
    }
}

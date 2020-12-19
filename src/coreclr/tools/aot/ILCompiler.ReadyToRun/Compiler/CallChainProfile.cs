﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ILCompiler.IBC;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace ILCompiler
{
    // This is a sample of the Json format the call chain data is stored in:
    //
    // {
    //   "state_size": 5,
    //   "chain": [
    //     [
    //       [
    //         "mscorlib.ni.dll!System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture",
    //         "mscorlib.ni.dll!System.Runtime.ExceptionServices.ExceptionDispatchInfo..ctor",
    //         "clr.dll!ExceptionNative::GetStackTracesDeepCopy",
    //         "clr.dll!ExceptionObject::GetStackTrace",
    //         "clr.dll!SpinLock::GetLock"
    //       ],
    //       [
    //         [
    //           "clr.dll!SpinLock::SpinToAcquire"
    //         ],
    //         [
    //           1
    //         ]
    //       ]
    //     ],
    //     [
    //       [
    //         "System.Core.ni.dll!System.Collections.Generic.HashSet`1[System.__Canon]..ctor",
    //         "System.Core.ni.dll!System.Collections.Generic.HashSet`1[System.__Canon].UnionWith",
    //         "System.Core.ni.dll!System.Linq.Enumerable+WhereSelectEnumerableIterator`2[System.__Canon,System.__Canon].MoveNext",
    //         "System.Core.ni.dll!System.Collections.Generic.HashSet`1[System.__Canon].Contains",
    //         "System.Core.ni.dll!System.Collections.Generic.HashSet`1[System.__Canon].InternalGetHashCode"
    //       ],
    //       [
    //         [
    //           "___END__",
    //           "mscorlib.ni.dll!System.Collections.Generic.GenericEqualityComparer`1[System.__Canon].GetHashCode"
    //         ],
    //         [
    //           2,
    //           3
    //         ]
    //       ]
    //     ]
    //   ]
    // }
    public class CallChainProfile
    {
        IEnumerable<ModuleDesc> _referenceableModules;
        Dictionary<MethodDesc, Dictionary<MethodDesc, int>> _resolvedProfileData;

        // Diagnostics
        private int _methodResolvesAttempted = 0;
        private int _methodsSuccessfullyResolved = 0;
        private Dictionary<string, int> _resolveFails = new Dictionary<string, int>();

        public CallChainProfile(string callChainProfileFile,
                                CompilerTypeSystemContext context,
                                IEnumerable<ModuleDesc> referenceableModules)
        {
            _referenceableModules = referenceableModules;
            var analysisData = ReadCallChainAnalysisData(callChainProfileFile);
            _resolvedProfileData = ResolveMethods(analysisData, context);
            WriteProfileParseStats();
        }

        /// <summary>
        /// Try to resolve each name from the profile data to a MethodDesc
        /// </summary>
        private Dictionary<MethodDesc, Dictionary<MethodDesc, int>> ResolveMethods(Dictionary<string, Dictionary<string, int>> profileData, CompilerTypeSystemContext context)
        {
            var resolvedProfileData = new Dictionary<MethodDesc, Dictionary<MethodDesc, int>>();
            Dictionary<string, MethodDesc> nameToMethodDescMap = new Dictionary<string, MethodDesc>();

            foreach (var keyAndMethods in profileData)
            {
                // Resolve the calling method
                var resolvedKeyMethod = CachedResolveMethodName(nameToMethodDescMap, keyAndMethods.Key, context);

                if (resolvedKeyMethod == null)
                    continue;

                // Resolve each callee and counts
                foreach (var methodAndHitCount in keyAndMethods.Value)
                {
                    var resolvedCalledMethod = CachedResolveMethodName(nameToMethodDescMap ,methodAndHitCount.Key, context);
                    if (resolvedCalledMethod == null)
                        continue;

                    if (!resolvedProfileData.ContainsKey(resolvedKeyMethod))
                    {
                        resolvedProfileData.Add(resolvedKeyMethod, new Dictionary<MethodDesc, int>());
                    }

                    if (!resolvedProfileData[resolvedKeyMethod].ContainsKey(resolvedCalledMethod))
                    {
                        resolvedProfileData[resolvedKeyMethod].Add(resolvedCalledMethod, 0);
                    }
                    resolvedProfileData[resolvedKeyMethod][resolvedCalledMethod] += methodAndHitCount.Value;
                }
            }

            return resolvedProfileData;
        }

        private int _cacheHits = 0;
        private int _cacheMisses = 0;
        private MethodDesc CachedResolveMethodName(Dictionary<string, MethodDesc> nameToMethodDescMap, string methodName, CompilerTypeSystemContext context)
        {
            MethodDesc resolvedMethod = null;
            if (nameToMethodDescMap.ContainsKey(methodName))
            {
                _cacheHits++;
                resolvedMethod = nameToMethodDescMap[methodName];
            }
            else
            {
                _cacheMisses++;
                resolvedMethod = ResolveMethodName(context, methodName);
                nameToMethodDescMap.Add(methodName, resolvedMethod);
            }

            if (resolvedMethod == null)
            {
                if (!_resolveFails.ContainsKey(methodName))
                {
                    _resolveFails.Add(methodName, 0);
                }
                _resolveFails[methodName]++;
            }
            return resolvedMethod;
        }

        private MethodDesc ResolveMethodName(CompilerTypeSystemContext context, string methodName)
        {
            // Example method name entries. Can we parse them as custom attribute formatted names?
            // mscorlib.ni.dll!System.Runtime.ExceptionServices.ExceptionDispatchInfo..ctor
            // System.Core.ni.dll!System.Linq.Enumerable+WhereSelectEnumerableIterator`2[System.__Canon,System.__Canon].MoveNext
            // Microsoft.Azure.Monitoring.WarmPath.FrontEnd.Middleware.SecurityMiddlewareBase`1+<Invoke>d__6[System.__Canon]!MoveNext
            // System.Runtime.CompilerServices.AsyncTaskMethodBuilder!Start
            _methodResolvesAttempted++;

            string[] splitMethodName = methodName.Split("!");
            if (splitMethodName.Length != 2)
            {
                return null;
            }

            if (splitMethodName[0].EndsWith(".dll") ||
                splitMethodName[0].EndsWith(".ni.dll") ||
                splitMethodName[0].EndsWith(".exe") ||
                splitMethodName[0].EndsWith(".ni.exe"))
            {
                // Native stack frame for the method name. This happens for managed methods in native images
                // (Remember, this is .NET Framework data we're starting with)
                string moduleSimpleName = Path.ChangeExtension(splitMethodName[0], null);
                // Desktop has native images with ni.dll or ni.exe extensions very frequently
                if (moduleSimpleName.EndsWith(".ni"))
                    moduleSimpleName = moduleSimpleName.Substring(0, moduleSimpleName.Length - 3);
                string unresolvedNamespaceTypeAndMethodName = splitMethodName[1];

                // Try to resolve the module from the list of loaded assemblies
                EcmaModule resolvedModule = context.GetModuleForSimpleName(moduleSimpleName, false);
                if (resolvedModule == null)
                    return null;

                // Resolve a name like System.Linq.Enumerable+WhereSelectEnumerableIterator`2[System.__Canon,System.__Canon].MoveNext
                // Take the string after the last period as the method name (special case for .ctor and .cctor)
                string namespaceAndTypeName = null;
                string methodNameWithoutType = null;

                if (unresolvedNamespaceTypeAndMethodName.EndsWith("..ctor"))
                {
                    namespaceAndTypeName = unresolvedNamespaceTypeAndMethodName.Substring(0, unresolvedNamespaceTypeAndMethodName.Length - "..ctor".Length);
                    methodNameWithoutType = ".ctor";
                }
                else if (unresolvedNamespaceTypeAndMethodName.EndsWith("..cctor"))
                {
                    namespaceAndTypeName = unresolvedNamespaceTypeAndMethodName.Substring(0, unresolvedNamespaceTypeAndMethodName.Length - "..cctor".Length);
                    methodNameWithoutType = ".cctor";
                }
                else
                {
                    int lastDotIndex = unresolvedNamespaceTypeAndMethodName.LastIndexOf(".");
                    if (lastDotIndex < 0)
                        return null;

                    namespaceAndTypeName = unresolvedNamespaceTypeAndMethodName.Substring(0, lastDotIndex);
                    methodNameWithoutType = unresolvedNamespaceTypeAndMethodName.Length > lastDotIndex ? unresolvedNamespaceTypeAndMethodName.Substring(lastDotIndex + 1) : "";
                }

                var resolvedMethod = ResolveMethodName(context, resolvedModule, namespaceAndTypeName, methodNameWithoutType);
                if (resolvedMethod != null)
                {
                    _methodsSuccessfullyResolved++;
                    return resolvedMethod;
                }
                    
            }
            else
            {
                // We have Namespace.Type!Method format with no method signature information. Check all loaded modules for a matching
                // type name, and the first method on that type with matching name.
                // Microsoft.Azure.Monitoring.WarmPath.FrontEnd.Middleware.SecurityMiddlewareBase`1+<Invoke>d__6[System.__Canon]!MoveNext
                // System.Runtime.CompilerServices.AsyncTaskMethodBuilder!Start
                string namespaceAndTypeName = splitMethodName[0];
                string methodNameWithoutType = splitMethodName[1];
                
                foreach (var module in _referenceableModules)
                {
                    var resolvedMethod = ResolveMethodName(context, module, namespaceAndTypeName, methodNameWithoutType);
                    if (resolvedMethod != null)
                    {
                        _methodsSuccessfullyResolved++;
                        return resolvedMethod;
                    }
                        
                }
            }

            return null;
        }

        /// <summary>
        /// Given a parsed out module, namespace + type, and method name, try to find a matching MethodDesc
        /// TODO: We have no signature information for the method - what policy should we apply where multiple methods exist with the same name
        /// but different signatures? For now we'll take the first matching and ignore others. Ideally we'll improve the profile data to include this.
        /// </summary>
        /// <returns>MethodDesc if found, null otherwise</returns>
        private MethodDesc ResolveMethodName(CompilerTypeSystemContext context, ModuleDesc module, string namespaceAndTypeName, string methodName)
        {
            TypeDesc resolvedType = module.GetTypeByCustomAttributeTypeName(namespaceAndTypeName, false, (typeDefName, module, throwIfNotFound) =>
            {
                return (MetadataType)context.GetCanonType(typeDefName)
                    ?? CustomAttributeTypeNameParser.ResolveCustomAttributeTypeDefinitionName(typeDefName, module, throwIfNotFound);
            });

            if (resolvedType != null)
            {
                var resolvedMethod = resolvedType.GetMethod(methodName, null);
                if (resolvedMethod != null)
                {
                    return resolvedMethod;
                }
            }

            return null;
        }

        private Dictionary<string, Dictionary<string, int>> ReadCallChainAnalysisData(string jsonProfileFile)
        {
            Dictionary<string, Dictionary<string, int>> profileData = new Dictionary<string, Dictionary<string, int>>();

            using (StreamReader stream = File.OpenText(jsonProfileFile))
            using (JsonDocument document = JsonDocument.Parse(stream.BaseStream))
            {
                JsonElement root = document.RootElement;
                JsonElement chainsRoot = root.GetProperty("chain");

                foreach (JsonElement chain in chainsRoot.EnumerateArray())
                {
                    // Each chain contains 2 arrays: the key (of chain length), a list of methods which follow the chain, a list of counts for each respective method
                    string keyParts = "";
                    bool readingKey = true;
                    foreach (JsonElement keyElement in chain.EnumerateArray())
                    {
                        if (readingKey)
                        {
                            // Build key method names
                            foreach (JsonElement keyPartElement in keyElement.EnumerateArray())
                            {
                                if (!keyPartElement.GetString().Equals("___BEGIN__"))
                                {
                                    keyParts = keyPartElement.GetString();
                                }
                            }
                            readingKey = false;
                        }
                        else
                        {
                            bool readingMethodNames = true;
                            List<string> followingMethodList = new List<string>();
                            foreach (JsonElement methodListArray in keyElement.EnumerateArray())
                            {
                                if (readingMethodNames)
                                {
                                    // Read the array of methods which follow the "keyParts" chain
                                    foreach (JsonElement followingMethods in methodListArray.EnumerateArray())
                                    {
                                        followingMethodList.Add(followingMethods.GetString());
                                    }

                                    readingMethodNames = false;
                                }
                                else
                                {
                                    // Read the array of call counts
                                    int index = 0;
                                    foreach (JsonElement methodCount in methodListArray.EnumerateArray())
                                    {
                                        if (string.IsNullOrEmpty(keyParts))
                                            break;

                                        if (!profileData.ContainsKey(keyParts))
                                        {
                                            profileData.Add(keyParts, new Dictionary<string, int>());
                                        }
                                        if (!profileData[keyParts].ContainsKey(followingMethodList[index]))
                                        {
                                            profileData[keyParts].Add(followingMethodList[index], methodCount.GetInt32());
                                        }
                                        else
                                        {
                                            profileData[keyParts][followingMethodList[index]] += methodCount.GetInt32();
                                        }
                                        index++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return profileData;
        }

        /// <summary>
        /// Dump diagnostic information to the console
        /// </summary>
        private void WriteProfileParseStats()
        {
            // Display the resolve fails ordered by how many times each method appeared in the trace
            // foreach (var fail in _resolveFails.OrderByDescending((kvp) => kvp.Value))
            // {
            //     Console.WriteLine($"{fail.Value}\t{fail.Key}");
            // }

            // Display all resolved methods in key -> { method -> count, method2 -> count} map
            foreach (var key in _resolvedProfileData)
            {
                Console.WriteLine($"{key.Key.ToString()}");

                foreach (var calledMethodAndCount in key.Value)
                {
                    Console.WriteLine($"\t{calledMethodAndCount.Key.ToString()} -> {calledMethodAndCount.Value} calls");
                }
            }

            Console.WriteLine($"Method resolves attempted: {_methodResolvesAttempted}");
            Console.WriteLine($"Successfully resolved {_methodsSuccessfullyResolved} methods ({(double)_methodsSuccessfullyResolved / (double)_methodResolvesAttempted:P})");

            int cacheTotal = _cacheHits + _cacheMisses;
            Console.WriteLine("Lookup cache performance:");
            Console.WriteLine($"Cache hits: {_cacheHits} ({(double)_cacheHits / (double)cacheTotal:P})");
            Console.WriteLine($"Cache misses: {_cacheMisses}");
        }

        public IReadOnlyDictionary<MethodDesc, Dictionary<MethodDesc, int>> ResolvedProfileData => _resolvedProfileData;
    }
}


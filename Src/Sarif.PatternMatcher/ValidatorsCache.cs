﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Microsoft.CodeAnalysis.Sarif.PatternMatcher
{
    public class ValidatorsCache
    {
        private static readonly object sync = new object();
        private static string assemblyBaseFolder;
        private readonly IFileSystem _fileSystem;
        private readonly Dictionary<string, Assembly> _resolvedNames;
        private Dictionary<string, ValidationMethodPair> _ruleNameToValidationMethods;

        public ValidatorsCache(IEnumerable<string> validatorBinaryPaths = null, IFileSystem fileSystem = null)
        {
            ValidatorPaths =
                validatorBinaryPaths != null
                    ? new HashSet<string>(validatorBinaryPaths, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _fileSystem = fileSystem ?? FileSystem.Instance;
            _resolvedNames = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        }

        public ISet<string> ValidatorPaths { get; }

        public Dictionary<string, ValidationMethodPair> RuleNameToValidationMethods
        {
            get
            {
                if (_ruleNameToValidationMethods == null)
                {
                    lock (sync)
                    {
                        if (_ruleNameToValidationMethods == null)
                        {
                            _ruleNameToValidationMethods ??= LoadValidationAssemblies(ValidatorPaths);
                        }
                    }
                }

                return _ruleNameToValidationMethods;
            }
        }

        public static ValidationMethodPair GetValidationMethodPair(string ruleName,
                                                                   Dictionary<string, ValidationMethodPair> ruleIdToMethodMap)
        {
            if (ruleName.Contains("/"))
            {
                ruleName = ruleName.Substring(ruleName.IndexOf("/") + 1);
            }

            string validatorName = ruleName + "Validator";

            ruleIdToMethodMap.TryGetValue(validatorName, out ValidationMethodPair validationPair);
            return validationPair;
        }

        public static Validation ValidateStaticHelper(MethodInfo isValidStaticMethodInfo,
                                                       ref string matchedPattern,
                                                       ref IDictionary<string, string> groups,
                                                       ref string failureLevel,
                                                       ref string fingerprint,
                                                       ref string message)
        {
            string validationText;

            object[] arguments = new object[]
            {
                matchedPattern,
                groups,
                failureLevel,
                fingerprint,
                message,
            };

            string currentDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory =
                    Path.GetDirectoryName(isValidStaticMethodInfo.ReflectedType.Assembly.Location);

                validationText =
                    (string)isValidStaticMethodInfo.Invoke(
                        obj: null, arguments);
            }
            finally
            {
                Environment.CurrentDirectory = currentDirectory;
            }

            matchedPattern = (string)arguments[0];
            groups = (Dictionary<string, string>)arguments[1];
            failureLevel = (string)arguments[2];
            fingerprint = (string)arguments[3];
            message = (string)arguments[4];

            if (!Enum.TryParse(validationText, out Validation result))
            {
                return Validation.ValidatorReturnedIllegalValidationState;
            }

            return result;
        }

        public static Validation ValidateDynamicHelper(MethodInfo isValidDynamicMethodInfo,
                                                        ref string fingerprint,
                                                        ref string message)
        {
            string validationText;

            object[] arguments = new object[]
            {
                fingerprint,
                message,
            };

            string currentDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory =
                    Path.GetDirectoryName(isValidDynamicMethodInfo.ReflectedType.Assembly.Location);

                validationText =
                    (string)isValidDynamicMethodInfo.Invoke(
                        obj: null, arguments);
            }
            finally
            {
                Environment.CurrentDirectory = currentDirectory;
            }

            fingerprint = (string)arguments[0];
            message = (string)arguments[1];

            if (!Enum.TryParse(validationText, out Validation result))
            {
                message = $"the unrecognized value was '{validationText}'";
                return Validation.ValidatorReturnedIllegalValidationState;
            }

            return result;
        }

        public Validation Validate(
            string ruleName,
            bool dynamicValidation,
            ref string matchedPattern,
            ref IDictionary<string, string> groups,
            ref string failureLevel,
            ref string fingerprint,
            ref string message,
            out bool pluginCanPerformDynamicAnalysis)
        {
            pluginCanPerformDynamicAnalysis = false;

            return ValidateHelper(
                RuleNameToValidationMethods,
                ruleName,
                dynamicValidation,
                ref matchedPattern,
                ref groups,
                ref failureLevel,
                ref fingerprint,
                ref message,
                out pluginCanPerformDynamicAnalysis);
        }

        internal static Validation ValidateHelper(
            Dictionary<string, ValidationMethodPair> ruleIdToMethodMap,
            string ruleName,
            bool dynamicValidation,
            ref string matchedPattern,
            ref IDictionary<string, string> groups,
            ref string failureLevel,
            ref string fingerprint,
            ref string message,
            out bool pluginCanPerformDynamicAnalysis)
        {
            pluginCanPerformDynamicAnalysis = false;
            fingerprint = null;
            message = null;

            ValidationMethodPair validationPair = GetValidationMethodPair(ruleName, ruleIdToMethodMap);

            if (validationPair == null)
            {
                return Validation.ValidatorNotFound;
            }

            Validation result = ValidateStaticHelper(validationPair.IsValidStatic,
                                                     ref matchedPattern,
                                                     ref groups,
                                                     ref failureLevel,
                                                     ref fingerprint,
                                                     ref message);

            pluginCanPerformDynamicAnalysis = validationPair.IsValidDynamic != null;

            return (result != Validation.NoMatch && result != Validation.Expired && dynamicValidation && pluginCanPerformDynamicAnalysis) ?
                ValidateDynamicHelper(validationPair.IsValidDynamic, ref fingerprint, ref message) :
                result;
        }

        private Dictionary<string, ValidationMethodPair> LoadValidationAssemblies(IEnumerable<string> validatorPaths)
        {
            var ruleToMethodMap = new Dictionary<string, ValidationMethodPair>();
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            foreach (string validatorPath in validatorPaths)
            {
                Assembly assembly = null;

                if (_fileSystem.FileExists(validatorPath))
                {
                    try
                    {
                        assemblyBaseFolder = Path.GetDirectoryName(validatorPath);
                        assembly = _fileSystem.AssemblyLoadFrom(validatorPath);
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // TODO log something here.
                    }

                    if (assembly == null) { continue; }

                    foreach (Type type in assembly.GetTypes())
                    {
                        string typeName = type.Name;

                        if (!typeName.EndsWith("Validator") || typeName.Equals("Validator"))
                        {
                            continue;
                        }

                        MethodInfo isValidStatic = type.GetMethod(
                            "IsValidStatic",
                            new[]
                            {
                                typeof(string).MakeByRefType(), // Matched pattern.
                                typeof(Dictionary<string, string>).MakeByRefType(), // Regex groups.
                                typeof(string).MakeByRefType(), // FailureLevel.
                                typeof(string).MakeByRefType(), // Fingerprint.
                                typeof(string).MakeByRefType(), // Message.
                            },
                            null);

                        if (isValidStatic == null || isValidStatic?.ReturnType != typeof(string))
                        {
                            continue;
                        }

                        MethodInfo isValidDynamic = type.GetMethod(
                            "IsValidDynamic",
                            new[]
                            {
                                typeof(string).MakeByRefType(), // Fingerprint.
                                typeof(string).MakeByRefType(), // Message.
                            },
                            null);

                        if (isValidDynamic?.ReturnType != typeof(string))
                        {
                            isValidDynamic = null;
                        }

                        ruleToMethodMap[typeName] = new ValidationMethodPair
                        {
                            IsValidStatic = isValidStatic,
                            IsValidDynamic = isValidDynamic,
                        };
                    }
                }
            }

            return ruleToMethodMap;
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            Assembly resolved = null;

            // We will only attempt to resolve an assembly a single time
            // to avoid re-entrance in cases where our logic below fails
            string assemblyName = args.Name.Split(',')[0];
            if (this._resolvedNames.ContainsKey(assemblyName))
            {
                return this._resolvedNames[assemblyName];
            }

            AppDomain currentDomain = AppDomain.CurrentDomain;
            Assembly[] assemblies = currentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (assembly.FullName.Split(',')[0] == assemblyName)
                {
                    return assembly;
                }
            }

            if (assemblyBaseFolder.EndsWith("analyze\\..\\bin"))
            {
                assemblyBaseFolder = assemblyBaseFolder.Replace("analyze\\..\\bin", string.Empty);
            }

            string presumedAssemblyPath = Path.Combine(assemblyBaseFolder, Path.GetFileName(assemblyName));

            if (!presumedAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !presumedAssemblyPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                presumedAssemblyPath += ".dll";

                if (!File.Exists(presumedAssemblyPath))
                {
                    // Strip .dll and give .exe a whirl
                    presumedAssemblyPath = Path.Combine(assemblyBaseFolder, assemblyName) + ".exe";
                }
            }

            if (File.Exists(presumedAssemblyPath))
            {
                try
                {
                    // If we use Assembly.LoadFrom, a FileLoadException
                    // saying that it could not load the file.
                    resolved = Assembly.Load(_fileSystem.FileReadAllBytes(presumedAssemblyPath));

                    this._resolvedNames.Add(assemblyName, resolved);
                }
                catch (IOException) { }
                catch (TypeLoadException) { }
                catch (BadImageFormatException) { }
                catch (UnauthorizedAccessException) { }
            }

            return resolved;
        }
    }
}

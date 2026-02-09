/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace Antigravity.Ide.Editor
{
    public interface IAssemblyNameProvider
    {
        string[] ProjectSupportedExtensions { get; }
        string ProjectGenerationRootNamespace { get; }
        ProjectGenerationFlag GenerationFlag { get; }

        string GetAssemblyNameFromScriptPath(string path);
        string GetAssemblyName(string assemblyOutputPath, string assemblyName);
        bool IsInternalizedPackagePath(string path);
        IEnumerable<Assembly> GetAssemblies(Func<string, bool> shouldFileBePartOfSolution);
        IEnumerable<string> GetAllAssetPaths();
        UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath);
        ResponseFileData ParseResponseFile(string responseFilePath, string projectDirectory, string[] systemReferenceDirectories);
        void ToggleProjectGeneration(ProjectGenerationFlag preference);
    }

    public class AssemblyNameProvider : IAssemblyNameProvider
    {
        private readonly Dictionary<string, UnityEditor.PackageManager.PackageInfo> m_PackageInfoCache = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>();

        ProjectGenerationFlag m_ProjectGenerationFlag = (ProjectGenerationFlag)EditorPrefs.GetInt(
            "antigravity_project_generation_flag",
            (int)(ProjectGenerationFlag.Local | ProjectGenerationFlag.Embedded));

        public string[] ProjectSupportedExtensions => UnityEditor.EditorSettings.projectGenerationUserExtensions;

        public string ProjectGenerationRootNamespace => UnityEditor.EditorSettings.projectGenerationRootNamespace;

        public ProjectGenerationFlag GenerationFlag
        {
            get { return ProjectGenerationFlagImpl; }
            private set { ProjectGenerationFlagImpl = value; }
        }

        internal virtual ProjectGenerationFlag ProjectGenerationFlagImpl
        {
            get => m_ProjectGenerationFlag;
            private set
            {
                EditorPrefs.SetInt("antigravity_project_generation_flag", (int)value);
                m_ProjectGenerationFlag = value;
            }
        }

        public string GetAssemblyNameFromScriptPath(string path)
        {
            return CompilationPipeline.GetAssemblyNameFromScriptPath(path);
        }

        internal static readonly string AssemblyOutput = Path.Combine("Temp", "bin", "Debug").NormalizePathSeparators();
        internal static readonly string PlayerAssemblyOutput = Path.Combine("Temp", "bin", "Debug", "Player").NormalizePathSeparators();

        public IEnumerable<Assembly> GetAssemblies(Func<string, bool> shouldFileBePartOfSolution)
        {
            IEnumerable<Assembly> assemblies = GetAssembliesByType(AssembliesType.Editor, shouldFileBePartOfSolution, AssemblyOutput);

            if (!GenerationFlag.HasFlag(ProjectGenerationFlag.PlayerAssemblies))
            {
                return assemblies;
            }
            var playerAssemblies = GetAssembliesByType(AssembliesType.Player, shouldFileBePartOfSolution, PlayerAssemblyOutput);
            return assemblies.Concat(playerAssemblies);
        }

        private static IEnumerable<Assembly> GetAssembliesByType(AssembliesType type, Func<string, bool> shouldFileBePartOfSolution, string outputPath)
        {
            foreach (var assembly in CompilationPipeline.GetAssemblies(type))
            {
                if (assembly.sourceFiles.Any(shouldFileBePartOfSolution))
                {
                    yield return new Assembly(
                        assembly.name,
                        outputPath,
                        assembly.sourceFiles,
                        assembly.defines,
                        assembly.assemblyReferences,
                        assembly.compiledAssemblyReferences,
                        assembly.flags,
                        assembly.compilerOptions
#if UNITY_2020_2_OR_NEWER
						, assembly.rootNamespace
#endif
                    );
                }
            }
        }

        public string GetCompileOutputPath(string assemblyName)
        {
            // We need to keep this one for API surface check (AssemblyNameProvider is public), but not used anymore
            throw new NotImplementedException();
        }

        public IEnumerable<string> GetAllAssetPaths()
        {
            return AssetDatabase.GetAllAssetPaths();
        }

        private static string ResolvePotentialParentPackageAssetPath(string assetPath)
        {
            const string packagesPrefix = "packages/";
            if (!assetPath.StartsWith(packagesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var followupSeparator = assetPath.IndexOf('/', packagesPrefix.Length);
            if (followupSeparator == -1)
            {
                return assetPath.ToLowerInvariant();
            }

            return assetPath.Substring(0, followupSeparator).ToLowerInvariant();
        }

        public UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath)
        {
            var parentPackageAssetPath = ResolvePotentialParentPackageAssetPath(assetPath);
            if (parentPackageAssetPath == null)
            {
                return null;
            }

            if (m_PackageInfoCache.TryGetValue(parentPackageAssetPath, out var cachedPackageInfo))
            {
                return cachedPackageInfo;
            }

            var result = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(parentPackageAssetPath);
            m_PackageInfoCache[parentPackageAssetPath] = result;
            return result;
        }

        public bool IsInternalizedPackagePath(string path)
        {
            if (string.IsNullOrEmpty(path.Trim()))
            {
                return false;
            }
            var packageInfo = FindForAssetPath(path);
            if (packageInfo == null)
            {
                return false;
            }
            var packageSource = packageInfo.source;
            switch (packageSource)
            {
                case PackageSource.Embedded:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.Embedded);
                case PackageSource.Registry:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.Registry);
                case PackageSource.BuiltIn:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.BuiltIn);
                case PackageSource.Unknown:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.Unknown);
                case PackageSource.Local:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.Local);
                case PackageSource.Git:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.Git);
                case PackageSource.LocalTarball:
                    return !GenerationFlag.HasFlag(ProjectGenerationFlag.LocalTarBall);
            }

            return false;
        }

        public ResponseFileData ParseResponseFile(string responseFilePath, string projectDirectory, string[] systemReferenceDirectories)
        {
            return CompilationPipeline.ParseResponseFile(
              responseFilePath,
              projectDirectory,
              systemReferenceDirectories
            );
        }

        public void ToggleProjectGeneration(ProjectGenerationFlag preference)
        {
            if (GenerationFlag.HasFlag(preference))
            {
                GenerationFlag ^= preference;
            }
            else
            {
                GenerationFlag |= preference;
            }
        }

        internal void ResetPackageInfoCache()
        {
            m_PackageInfoCache.Clear();
        }

        public void ResetProjectGenerationFlag()
        {
            GenerationFlag = ProjectGenerationFlag.None;
        }

        public string GetAssemblyName(string assemblyOutputPath, string assemblyName)
        {
            if (assemblyOutputPath == PlayerAssemblyOutput)
                return assemblyName + ".Player";

            return assemblyName;
        }
    }
}

/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
    public enum ScriptingLanguage
    {
        None,
        CSharp
    }

    public interface IGenerator
    {
        bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles);
        void Sync();
        bool HasSolutionBeenGenerated();
        bool IsSupportedFile(string path);
        string SolutionFile();
        string ProjectDirectory { get; }
        IAssemblyNameProvider AssemblyNameProvider { get; }
    }

    public class ProjectGeneration : IGenerator
    {
        public static readonly string MSBuildNamespaceUri = "http://schemas.microsoft.com/developer/msbuild/2003";

        public IAssemblyNameProvider AssemblyNameProvider => m_AssemblyNameProvider;
        public string ProjectDirectory { get; }

        internal const string k_WindowsNewline = "\r\n";

        const string m_SolutionProjectEntryTemplate = @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""{4}EndProject";

        HashSet<string> _supportedExtensions;

        readonly string m_ProjectName;
        internal readonly IAssemblyNameProvider m_AssemblyNameProvider;
        readonly IFileIO m_FileIOProvider;
        readonly IGUIDGenerator m_GUIDGenerator;

        public ProjectGeneration() : this(Directory.GetParent(Application.dataPath).FullName)
        {
        }

        public ProjectGeneration(string tempDirectory) : this(tempDirectory, new AssemblyNameProvider(), new FileIOProvider(), new GUIDProvider())
        {
        }

        public ProjectGeneration(string tempDirectory, IAssemblyNameProvider assemblyNameProvider, IFileIO fileIoProvider, IGUIDGenerator guidGenerator)
        {
            ProjectDirectory = FileUtility.NormalizeWindowsToUnix(tempDirectory);
            m_ProjectName = Path.GetFileName(ProjectDirectory);
            m_AssemblyNameProvider = assemblyNameProvider;
            m_FileIOProvider = fileIoProvider;
            m_GUIDGenerator = guidGenerator;

            SetupProjectSupportedExtensions();
        }

        public bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            SetupProjectSupportedExtensions();

            // Don't sync if we haven't synced before
            var affected = affectedFiles as ICollection<string> ?? affectedFiles.ToArray();
            var reimported = reimportedFiles as ICollection<string> ?? reimportedFiles.ToArray();
            if (!HasFilesBeenModified(affected, reimported))
            {
                return false;
            }

            var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution);
            var allProjectAssemblies = RelevantAssembliesForMode(assemblies).ToList();
            SyncSolution(allProjectAssemblies);

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            var affectedNames = affected
                .Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name =>
                    name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            var reimportedNames = reimported
                .Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name =>
                    name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            var affectedAndReimported = new HashSet<string>(affectedNames.Concat(reimportedNames));

            foreach (var assembly in allProjectAssemblies)
            {
                if (!affectedAndReimported.Contains(assembly.name))
                    continue;

                SyncProject(assembly,
                    allAssetProjectParts,
                    ParseResponseFileData(assembly));
            }

            return true;
        }

        private bool HasFilesBeenModified(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        }

        private static bool ShouldSyncOnReimportedAsset(string asset)
        {
            var extension = Path.GetExtension(asset);
            return extension == ".dll" || extension == ".asmdef";
        }

        public void Sync()
        {
            SetupProjectSupportedExtensions();

            (m_AssemblyNameProvider as AssemblyNameProvider)?.ResetPackageInfoCache();

            var externalCodeAlreadyGeneratedProjects = OnPreGeneratingCSProjectFiles();

            if (!externalCodeAlreadyGeneratedProjects)
            {
                GenerateAndWriteSolutionAndProjects();
            }

            OnGeneratedCSProjectFiles();
        }

        public bool HasSolutionBeenGenerated()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        private void SetupProjectSupportedExtensions()
        {
            _supportedExtensions = new HashSet<string>
            {
                "dll",
                "asmdef",
                "additionalfile"
            };

            foreach (var extension in m_AssemblyNameProvider.ProjectSupportedExtensions)
            {
                _supportedExtensions.Add(extension);
            }

            // Fallback if EditorSettings.projectGenerationBuiltinExtensions is not available or empty?
            // Usually valid in recent Unity versions.
            try
            {
                foreach (var extension in UnityEditor.EditorSettings.projectGenerationBuiltinExtensions)
                {
                    _supportedExtensions.Add(extension);
                }
            }
            catch { }
        }

        private bool ShouldFileBePartOfSolution(string file)
        {
            if (m_AssemblyNameProvider.IsInternalizedPackagePath(file))
            {
                return false;
            }

            return IsSupportedFile(file);
        }

        private static string GetExtensionWithoutDot(string path)
        {
            if (!Path.HasExtension(path))
                return path;

            return Path
                .GetExtension(path)
                .TrimStart('.')
                .ToLower();
        }

        public bool IsSupportedFile(string path)
        {
            return IsSupportedFile(path, out _);
        }

        private bool IsSupportedFile(string path, out string extensionWithoutDot)
        {
            extensionWithoutDot = GetExtensionWithoutDot(path);
            return _supportedExtensions.Contains(extensionWithoutDot);
        }

        private static ScriptingLanguage ScriptingLanguageFor(Assembly assembly)
        {
            var files = assembly.sourceFiles;

            if (files.Length == 0)
                return ScriptingLanguage.None;

            return ScriptingLanguageForFile(files[0]);
        }

        internal static ScriptingLanguage ScriptingLanguageForExtension(string extensionWithoutDot)
        {
            return extensionWithoutDot == "cs" ? ScriptingLanguage.CSharp : ScriptingLanguage.None;
        }

        internal static ScriptingLanguage ScriptingLanguageForFile(string path)
        {
            return ScriptingLanguageForExtension(GetExtensionWithoutDot(path));
        }

        public void GenerateAndWriteSolutionAndProjects()
        {
            var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution).ToList();

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            SyncSolution(assemblies);

            var allProjectAssemblies = RelevantAssembliesForMode(assemblies);

            foreach (var assembly in allProjectAssemblies)
            {
                SyncProject(assembly,
                    allAssetProjectParts,
                    ParseResponseFileData(assembly));
            }
        }

        private ResponseFileData[] ParseResponseFileData(Assembly assembly)
        {
            var systemReferenceDirectories = CompilationPipeline.GetSystemAssemblyDirectories(assembly.compilerOptions.ApiCompatibilityLevel);

            var responseFilesData = new Dictionary<string, ResponseFileData>();
            if (assembly.compilerOptions.ResponseFiles != null)
            {
                foreach (var x in assembly.compilerOptions.ResponseFiles)
                {
                    if (!responseFilesData.ContainsKey(x))
                    {
                        responseFilesData.Add(x, m_AssemblyNameProvider.ParseResponseFile(
                            x,
                            ProjectDirectory,
                            systemReferenceDirectories
                        ));
                    }
                }
            }

            return responseFilesData.Select(x => x.Value).ToArray();
        }

        private Dictionary<string, string> GenerateAllAssetProjectParts()
        {
            Dictionary<string, StringBuilder> stringBuilders = new Dictionary<string, StringBuilder>();

            foreach (string asset in m_AssemblyNameProvider.GetAllAssetPaths())
            {
                if (m_AssemblyNameProvider.IsInternalizedPackagePath(asset))
                {
                    continue;
                }

                if (IsSupportedFile(asset, out var extensionWithoutDot) && ScriptingLanguage.None == ScriptingLanguageForExtension(extensionWithoutDot))
                {
                    var assemblyName = m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset);

                    if (string.IsNullOrEmpty(assemblyName))
                    {
                        continue;
                    }

                    assemblyName = Path.GetFileNameWithoutExtension(assemblyName);

                    if (!stringBuilders.TryGetValue(assemblyName, out var projectBuilder))
                    {
                        projectBuilder = new StringBuilder();
                        stringBuilders[assemblyName] = projectBuilder;
                    }

                    IncludeAsset(projectBuilder, IncludeAssetTag.None, asset);
                }
            }

            var result = new Dictionary<string, string>();

            foreach (var entry in stringBuilders)
                result[entry.Key] = entry.Value.ToString();

            return result;
        }

        internal enum IncludeAssetTag
        {
            Compile,
            None
        }

        internal virtual void IncludeAsset(StringBuilder builder, IncludeAssetTag tag, string asset)
        {
            var filename = EscapedRelativePathFor(asset, out var packageInfo);

            builder.Append("    <").Append(tag).Append(@" Include=""").Append(filename);
            if (Path.IsPathRooted(filename) && packageInfo != null)
            {
                var linkPath = SkipPathPrefix(asset.NormalizePathSeparators(), packageInfo.assetPath.NormalizePathSeparators());

                builder.Append(@""">").Append(k_WindowsNewline);
                builder.Append("      <Link>").Append(linkPath).Append("</Link>").Append(k_WindowsNewline);
                builder.Append($"    </{tag}>").Append(k_WindowsNewline);
            }
            else
            {
                builder.Append(@""" />").Append(k_WindowsNewline);
            }
        }

        private void SyncProject(
            Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            ResponseFileData[] responseFileData)
        {
            SyncProjectFileIfNotChanged(
                ProjectFile(assembly),
                ProjectText(assembly, allAssetsProjectParts, responseFileData));
        }

        private void SyncProjectFileIfNotChanged(string path, string newContents)
        {
            if (Path.GetExtension(path) == ".csproj")
            {
                newContents = OnGeneratedCSProject(path, newContents);
            }

            SyncFileIfNotChanged(path, newContents);
        }

        private void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            newContents = OnGeneratedSlnSolution(path, newContents);

            SyncFileIfNotChanged(path, newContents);
        }

        static void OnGeneratedCSProjectFiles()
        {
            foreach (var method in TypeCacheHelper.GetPostProcessorCallbacks(nameof(OnGeneratedCSProjectFiles)))
            {
                method.Invoke(null, Array.Empty<object>());
            }
        }

        private static bool OnPreGeneratingCSProjectFiles()
        {
            bool result = false;

            foreach (var method in TypeCacheHelper.GetPostProcessorCallbacks(nameof(OnPreGeneratingCSProjectFiles)))
            {
                var retValue = method.Invoke(null, Array.Empty<object>());
                if (method.ReturnType == typeof(bool))
                {
                    result |= (bool)retValue;
                }
            }

            return result;
        }

        private static string InvokeAssetPostProcessorGenerationCallbacks(string name, string path, string content)
        {
            foreach (var method in TypeCacheHelper.GetPostProcessorCallbacks(name))
            {
                var args = new[] { path, content };
                var returnValue = method.Invoke(null, args);
                if (method.ReturnType == typeof(string))
                {
                    content = (string)returnValue;
                }
            }

            return content;
        }

        private static string OnGeneratedCSProject(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(nameof(OnGeneratedCSProject), path, content);
        }

        private static string OnGeneratedSlnSolution(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(nameof(OnGeneratedSlnSolution), path, content);
        }

        private void SyncFileIfNotChanged(string filename, string newContents)
        {
            try
            {
                if (m_FileIOProvider.Exists(filename) && newContents == m_FileIOProvider.ReadAllText(filename))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            m_FileIOProvider.WriteAllText(filename, newContents);
        }

        private string ProjectText(Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            ResponseFileData[] responseFileData)
        {
            ProjectHeader(assembly, responseFileData, out StringBuilder projectBuilder);

            var references = new List<string>();

            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
            foreach (string file in assembly.sourceFiles)
            {
                if (!IsSupportedFile(file, out var extensionWithoutDot))
                    continue;

                if ("dll" != extensionWithoutDot)
                {
                    IncludeAsset(projectBuilder, IncludeAssetTag.Compile, file);
                }
                else
                {
                    var fullFile = EscapedRelativePathFor(file, out _);
                    references.Add(fullFile);
                }
            }
            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            if (allAssetsProjectParts.TryGetValue(assembly.name, out var additionalAssetsForProject))
            {
                projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);

                projectBuilder.Append(additionalAssetsForProject);

                projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            }

            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);

            var responseRefs = responseFileData.SelectMany(x => x.FullPathReferences.Select(r => r));
            var internalAssemblyReferences = assembly.assemblyReferences
                .Where(i => !i.sourceFiles.Any(ShouldFileBePartOfSolution)).Select(i => i.outputPath);
            var allReferences =
                assembly.compiledAssemblyReferences
                    .Union(responseRefs)
                    .Union(references)
                    .Union(internalAssemblyReferences);

            var definedReferences = new HashSet<string>();
            foreach (var reference in allReferences)
            {
                string fullReference = Path.IsPathRooted(reference) ? reference : Path.Combine(ProjectDirectory, reference);
                var escapedFullPath = EscapedRelativePathFor(fullReference, out _);
                var refName = Path.GetFileNameWithoutExtension(escapedFullPath);
                if (definedReferences.Contains(refName)) continue;
                definedReferences.Add(refName);
                AppendReference(fullReference, projectBuilder);
            }

            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            if (0 < assembly.assemblyReferences.Length)
            {
                projectBuilder.Append("  <ItemGroup>").Append(k_WindowsNewline);
                foreach (var reference in assembly.assemblyReferences.Where(i => i.sourceFiles.Any(ShouldFileBePartOfSolution)))
                {
                    AppendProjectReference(assembly, reference, projectBuilder);
                }

                projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
            }

            GetProjectFooter(projectBuilder);
            return projectBuilder.ToString();
        }

        private static string XmlFilename(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            path = path.Replace(@"%", "%25");
            path = path.Replace(@";", "%3b");

            return XmlEscape(path);
        }

        private static string XmlEscape(string s)
        {
            return SecurityElement.Escape(s);
        }

        internal virtual void AppendProjectReference(Assembly assembly, Assembly reference, StringBuilder projectBuilder)
        {
            var referenceName = m_AssemblyNameProvider.GetAssemblyName(reference.outputPath, reference.name);
            var guid = m_GUIDGenerator.ProjectGuid(m_ProjectName, referenceName);
            projectBuilder.Append(@"    <ProjectReference Include=""").Append(referenceName).Append(@".csproj"">").Append(k_WindowsNewline);
            projectBuilder.Append("      <Project>{").Append(guid).Append(@"}</Project>").Append(k_WindowsNewline);
            projectBuilder.Append("      <Name>").Append(referenceName).Append(@"</Name>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    </ProjectReference>").Append(k_WindowsNewline);
        }

        private void AppendReference(string fullReference, StringBuilder projectBuilder)
        {
            var escapedFullPath = EscapedRelativePathFor(fullReference, out _);
            projectBuilder.Append(@"    <Reference Include=""").Append(Path.GetFileNameWithoutExtension(escapedFullPath)).Append(@""">").Append(k_WindowsNewline);
            projectBuilder.Append("      <HintPath>").Append(escapedFullPath).Append("</HintPath>").Append(k_WindowsNewline);
            projectBuilder.Append("      <Private>False</Private>").Append(k_WindowsNewline);
            projectBuilder.Append("    </Reference>").Append(k_WindowsNewline);
        }

        public string ProjectFile(Assembly assembly)
        {
            return Path.Combine(ProjectDirectory, $"{m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name)}.csproj");
        }

#if UNITY_EDITOR_WIN
        private static readonly Regex InvalidCharactersRegexPattern = new Regex(@"\?|&|\*|""|<|>|\||#|%|\^|;", RegexOptions.Compiled);
#else
        private static readonly Regex InvalidCharactersRegexPattern = new Regex(@"\?|&|\*|""|<|>|\||#|%|\^|;|:", RegexOptions.Compiled);
#endif

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory.NormalizePathSeparators(), $"{InvalidCharactersRegexPattern.Replace(m_ProjectName, "_")}.sln");
        }

        internal string GetLangVersion(Assembly assembly, ResponseFileData[] responseFileData)
        {
            var langVersion = GetOtherArguments(responseFileData, "langversion").FirstOrDefault();
            if (!string.IsNullOrEmpty(langVersion))
                return langVersion;
            return "latest";
        }

        private static IEnumerable<string> GetOtherArguments(ResponseFileData[] responseFileData, string name)
        {
            var lines = responseFileData
                .SelectMany(x => x.OtherArguments)
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("/") || l.StartsWith("-"));

            foreach (var argument in lines)
            {
                var index = argument.IndexOf(":", StringComparison.Ordinal);
                if (index == -1)
                    continue;

                var key = argument
                    .Substring(1, index - 1)
                    .Trim();

                if (name != key)
                    continue;

                if (argument.Length <= index)
                    continue;

                yield return argument
                    .Substring(index + 1)
                    .Trim();
            }
        }

        private string ToNormalizedPath(string path)
        {
            return path
                .MakeAbsolutePath()
                .NormalizePathSeparators();
        }

        private string[] ToNormalizedPaths(IEnumerable<string> values)
        {
            return values
                .Where(a => !string.IsNullOrEmpty(a))
                .Select(a => ToNormalizedPath(a))
                .Distinct()
                .ToArray();
        }

        private void ProjectHeader(
            Assembly assembly,
            ResponseFileData[] responseFileData,
            out StringBuilder headerBuilder
        )
        {
            var projectType = ProjectTypeOf(assembly.name);

            var projectProperties = new ProjectProperties
            {
                ProjectGuid = m_GUIDGenerator.ProjectGuid(m_ProjectName, m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name)),
                LangVersion = GetLangVersion(assembly, responseFileData),
                AssemblyName = m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name),
                RootNamespace = GetRootNamespace(assembly),
                OutputPath = assembly.outputPath,
                Defines = assembly.defines.Concat(responseFileData.SelectMany(x => x.Defines)).Distinct().ToArray(),
                Unsafe = assembly.compilerOptions.AllowUnsafeCode | responseFileData.Any(x => x.Unsafe),
                FlavoringProjectType = projectType + ":" + (int)projectType,
                FlavoringBuildTarget = EditorUserBuildSettings.activeBuildTarget + ":" + (int)EditorUserBuildSettings.activeBuildTarget,
                FlavoringUnityVersion = Application.unityVersion,
                FlavoringPackageVersion = "1.0.0", // Placeholder for Antigravity package version
            };

            // Basic Analyzers support from Unity compiler options
#if UNITY_2020_2_OR_NEWER
            projectProperties.Analyzers = ToNormalizedPaths(assembly.compilerOptions.RoslynAnalyzerDllPaths);
            projectProperties.RulesetPath = ToNormalizedPath(assembly.compilerOptions.RoslynAnalyzerRulesetPath);
#endif

            GetProjectHeader(projectProperties, out headerBuilder);
        }

        private enum ProjectType
        {
            GamePlugins = 3,
            Game = 1,
            EditorPlugins = 7,
            Editor = 5,
        }

        private static ProjectType ProjectTypeOf(string fileName)
        {
            var plugins = fileName.Contains("firstpass");
            var editor = fileName.Contains("Editor");

            if (plugins && editor)
                return ProjectType.EditorPlugins;
            if (plugins)
                return ProjectType.GamePlugins;
            if (editor)
                return ProjectType.Editor;

            return ProjectType.Game;
        }

        internal virtual void GetProjectHeader(ProjectProperties properties, out StringBuilder headerBuilder)
        {
            headerBuilder = new StringBuilder();
            headerBuilder.Append(@"<?xml version=""1.0"" encoding=""utf-8""?>").Append(k_WindowsNewline);
            headerBuilder.Append(@"<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">").Append(k_WindowsNewline);
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <LangVersion>").Append(properties.LangVersion).Append(@"</LangVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProductVersion>10.0.20506</ProductVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <SchemaVersion>2.0</SchemaVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <RootNamespace>").Append(properties.RootNamespace).Append(@"</RootNamespace>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProjectGuid>{").Append(properties.ProjectGuid).Append(@"}</ProjectGuid>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AssemblyName>").Append(properties.AssemblyName).Append(@"</AssemblyName>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <FileAlignment>512</FileAlignment>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <BaseDirectory>.</BaseDirectory>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoConfig>true</NoConfig>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoStdLib>true</NoStdLib>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ImplicitlyExpandNETStandardFacades>false</ImplicitlyExpandNETStandardFacades>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ImplicitlyExpandDesignTimeFacades>false</ImplicitlyExpandDesignTimeFacades>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "">").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugSymbols>true</DebugSymbols>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugType>full</DebugType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Optimize>false</Optimize>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputPath>").Append(properties.OutputPath).Append(@"</OutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DefineConstants>").Append(string.Join(";", properties.Defines)).Append(@"</DefineConstants>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "">").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugType>pdbonly</DebugType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Optimize>true</Optimize>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputPath>Temp\bin\Release\</OutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            // Standard Analyzers property group
            if (!string.IsNullOrEmpty(properties.RulesetPath) || properties.Analyzers.Any())
            {
                headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
                if (!string.IsNullOrEmpty(properties.RulesetPath))
                    headerBuilder.Append(@"    <CodeAnalysisRuleSet>").Append(properties.RulesetPath).Append(@"</CodeAnalysisRuleSet>").Append(k_WindowsNewline);
                headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);
            }

            if (properties.Analyzers.Any())
            {
                headerBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
                foreach (var analyzer in properties.Analyzers)
                {
                    headerBuilder.Append(@"    <Analyzer Include=""").Append(analyzer).Append(@""" />").Append(k_WindowsNewline);
                }
                headerBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
            }
        }

        private static string GetRootNamespace(Assembly assembly)
        {
#if UNITY_2020_2_OR_NEWER
            return assembly.rootNamespace;
#else
            return EditorSettings.projectGenerationRootNamespace;
#endif
        }

        private void GetProjectFooter(StringBuilder projectBuilder)
        {
            projectBuilder.Append(@"  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />").Append(k_WindowsNewline);
            projectBuilder.Append(@"  <!-- To modify your build process, add your task inside one of the targets below and uncomment it. ").Append(k_WindowsNewline);
            projectBuilder.Append(@"       Other similar extension points exist, see Microsoft.Common.targets.").Append(k_WindowsNewline);
            projectBuilder.Append(@"  <Target Name=""BeforeBuild"">").Append(k_WindowsNewline);
            projectBuilder.Append(@"  </Target>").Append(k_WindowsNewline);
            projectBuilder.Append(@"  <Target Name=""AfterBuild"">").Append(k_WindowsNewline);
            projectBuilder.Append(@"  </Target>").Append(k_WindowsNewline);
            projectBuilder.Append(@"  -->").Append(k_WindowsNewline);
            projectBuilder.Append(@"</Project>").Append(k_WindowsNewline);
        }

        private string EscapedRelativePathFor(string file, out UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            var projectPath = FileUtility.GetAbsolutePath(ProjectDirectory);
            packageInfo = m_AssemblyNameProvider.FindForAssetPath(file);
            var fileFullPath = FileUtility.GetAbsolutePath(file);

            // Basic relative path logic
            if (fileFullPath.StartsWith(projectPath))
            {
                return fileFullPath.Substring(projectPath.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('/', '\\');
            }

            return fileFullPath;
        }

        private static string SkipPathPrefix(string path, string prefix)
        {
            if (path.StartsWith($@"{prefix}{Path.DirectorySeparatorChar}") || path.StartsWith($@"{prefix}/"))
                return path.Substring(prefix.Length + 1);
            return path;
        }

        internal static IEnumerable<Assembly> RelevantAssembliesForMode(IEnumerable<Assembly> assemblies)
        {
            return assemblies.Where(assembly => assembly.sourceFiles.Any(shouldFileBePartOfSolution));
        }

        private static bool shouldFileBePartOfSolution(string file)
        {
            // Simple check, can be improved
            return Path.GetExtension(file) == ".cs";
        }

        private void SyncSolution(IEnumerable<Assembly> assemblies)
        {
            SyncSolutionFileIfNotChanged(SolutionFile(), SolutionText(assemblies, SetStartupProject(assemblies)));
        }

        private string SolutionText(IEnumerable<Assembly> assemblies, Assembly startupProject)
        {
            var sb = new StringBuilder();
            sb.Append(@"Microsoft Visual Studio Solution File, Format Version 12.00").Append(k_WindowsNewline);
            sb.Append(@"# Visual Studio 15").Append(k_WindowsNewline);
            sb.Append(@"VisualStudioVersion = 15.0.28307.1267").Append(k_WindowsNewline);
            sb.Append(@"MinimumVisualStudioVersion = 10.0.40219.1").Append(k_WindowsNewline);

            var relevantAssemblies = RelevantAssembliesForMode(assemblies).ToList();

            foreach (var assembly in relevantAssemblies)
            {
                var assemblyName = m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name);
                var projectGuid = m_GUIDGenerator.ProjectGuid(m_ProjectName, assemblyName);
                sb.AppendFormat(m_SolutionProjectEntryTemplate, "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC", assemblyName, $"{assemblyName}.csproj", projectGuid, k_WindowsNewline);
            }

            sb.Append(@"Global").Append(k_WindowsNewline);

            sb.Append(@"    GlobalSection(SolutionConfigurationPlatforms) = preSolution").Append(k_WindowsNewline);
            sb.Append(@"        Debug|Any CPU = Debug|Any CPU").Append(k_WindowsNewline);
            sb.Append(@"        Release|Any CPU = Release|Any CPU").Append(k_WindowsNewline);
            sb.Append(@"    EndGlobalSection").Append(k_WindowsNewline);

            sb.Append(@"    GlobalSection(ProjectConfigurationPlatforms) = postSolution").Append(k_WindowsNewline);
            foreach (var assembly in relevantAssemblies)
            {
                var assemblyName = m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name);
                var projectGuid = m_GUIDGenerator.ProjectGuid(m_ProjectName, assemblyName);
                sb.AppendFormat(@"        {{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU{1}", projectGuid, k_WindowsNewline);
                sb.AppendFormat(@"        {{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU{1}", projectGuid, k_WindowsNewline);
                sb.AppendFormat(@"        {{{0}}}.Release|Any CPU.ActiveCfg = Release|Any CPU{1}", projectGuid, k_WindowsNewline);
                sb.AppendFormat(@"        {{{0}}}.Release|Any CPU.Build.0 = Release|Any CPU{1}", projectGuid, k_WindowsNewline);
            }
            sb.Append(@"    EndGlobalSection").Append(k_WindowsNewline);

            sb.Append(@"    GlobalSection(SolutionProperties) = preSolution").Append(k_WindowsNewline);
            sb.Append(@"        HideSolutionNode = FALSE").Append(k_WindowsNewline);
            sb.Append(@"    EndGlobalSection").Append(k_WindowsNewline);

            sb.Append(@"EndGlobal").Append(k_WindowsNewline);

            return sb.ToString();
        }

        private Assembly SetStartupProject(IEnumerable<Assembly> assemblies)
        {
            return assemblies.FirstOrDefault(a => a.name == "Assembly-CSharp");
        }
    }
}

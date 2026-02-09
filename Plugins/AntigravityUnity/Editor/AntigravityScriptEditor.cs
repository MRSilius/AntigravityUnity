using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Antigravity.Ide.Editor;
using Unity.CodeEditor;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
    [InitializeOnLoad]
    public class AntigravityScriptEditor : IExternalCodeEditor
    {
        const string EditorName = "Antigravity";
        const string EditorDisplayName = "Antigravity IDE";

        static readonly string[] KnownPaths =
        {
        "/Applications/Antigravity.app",
        "/Applications/Antigravity.app/Contents/MacOS/Antigravity",
        "C:\\Program Files\\Antigravity\\Antigravity.exe",
        "C:\\Program Files (x86)\\Antigravity\\Antigravity.exe",
        "D:\\Antigravity\\Antigravity.exe",
        "/usr/local/bin/antigravity",
        "/opt/homebrew/bin/antigravity",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity", "Antigravity.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity", "Antigravity.exe")
    };

        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".cpp", ".c", ".h", ".hpp", ".java", ".py",
        ".shader", ".cg", ".hlsl", ".glsl", ".txt", ".json", ".xml", ".yaml", ".yml",
        ".md", ".asm", ".asmdef", ".csproj", ".sln", ".targets", ".props"
    };

        private readonly IGenerator _projectGeneration;

        static AntigravityScriptEditor()
        {
            try
            {
                var editor = new AntigravityScriptEditor();
                CodeEditor.Register(editor);

                if (IsAntigravityInstalled())
                {
                    string currentEditorPath = CodeEditor.CurrentEditorInstallation;

                    if (string.IsNullOrEmpty(currentEditorPath) ||
                        !currentEditorPath.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                    {
                        var installations = editor.Installations;
                        if (installations.Length > 0)
                        {
                            CodeEditor.SetExternalScriptEditor(installations[0].Path);
                            editor.SyncAll();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }

        public AntigravityScriptEditor()
        {
            _projectGeneration = new ProjectGeneration();
        }

        private static bool IsAntigravityInstalled()
        {
            return KnownPaths.Any(p => File.Exists(p) || Directory.Exists(p));
        }

        public CodeEditor.Installation[] Installations
        {
            get
            {
                return KnownPaths
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .Select(p => new CodeEditor.Installation
                    {
                        Name = EditorDisplayName,
                        Path = p
                    })
                    .ToArray();
            }
        }

        public void Initialize(string editorInstallationPath)
        {
        }

        public void OnGUI()
        {
            GUILayout.Label("Antigravity IDE Settings", EditorStyles.boldLabel);
            GUILayout.Label("Configure which packages to include in the project", EditorStyles.miniLabel);

            EditorGUILayout.Space();

            var provider = _projectGeneration.AssemblyNameProvider;
            var flags = provider.GenerationFlag;

            EditorGUI.BeginChangeCheck();
            bool embedded = EditorGUILayout.Toggle("Embedded Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.Embedded));
            bool local = EditorGUILayout.Toggle("Local Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.Local));
            bool registry = EditorGUILayout.Toggle("Registry Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.Registry));
            bool git = EditorGUILayout.Toggle("Git Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.Git));
            bool builtIn = EditorGUILayout.Toggle("Built-in Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.BuiltIn));
            bool tarball = EditorGUILayout.Toggle("Local Tarball", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.LocalTarBall));
            bool unknown = EditorGUILayout.Toggle("Unknown Packages", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.Unknown));
            bool player = EditorGUILayout.Toggle("Player Projects", flags.HasFlag(Antigravity.Ide.Editor.ProjectGenerationFlag.PlayerAssemblies));

            if (EditorGUI.EndChangeCheck())
            {
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.Embedded, embedded);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.Local, local);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.Registry, registry);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.Git, git);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.BuiltIn, builtIn);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.LocalTarBall, tarball);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.Unknown, unknown);
                UpdateFlag(provider, Antigravity.Ide.Editor.ProjectGenerationFlag.PlayerAssemblies, player);
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Regenerate Project Files", GUILayout.Width(180)))
            {
                _projectGeneration.Sync();
                EditorUtility.DisplayDialog("Success", "Project files regenerated.", "OK");
            }

            if (GUILayout.Button("Open Antigravity", GUILayout.Width(180)))
            {
                OpenProject(null, -1, -1);
            }
        }

        void UpdateFlag(IAssemblyNameProvider provider, Antigravity.Ide.Editor.ProjectGenerationFlag flag, bool enabled)
        {
            // Use GenerationFlag (renamed property) to fix ambiguity
            bool currentState = provider.GenerationFlag.HasFlag(flag);
            if (currentState != enabled)
            {
                provider.ToggleProjectGeneration(flag);
            }
        }

        public bool OpenProject(string filePath, int line, int column)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                string extension = Path.GetExtension(filePath);
                if (!string.IsNullOrEmpty(extension) && !CodeExtensions.Contains(extension))
                {
                    return false;
                }
            }

            string installation = CodeEditor.CurrentEditorInstallation;

            if (string.IsNullOrEmpty(installation) || !installation.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrEmpty(filePath) || Directory.Exists(filePath))
            {
                filePath = Directory.GetCurrentDirectory();
            }
            else if (!File.Exists(filePath))
            {
                filePath = Directory.GetCurrentDirectory();
            }

            // Ensure project execution
            _projectGeneration.Sync();

            try
            {
                string executablePath = GetExecutablePath(installation);

                if (string.IsNullOrEmpty(executablePath))
                {
                    return false;
                }

                string arguments;
                if (Directory.Exists(filePath))
                {
                    arguments = $"\"{filePath}\"";
                }
                else
                {
                    arguments = $"-g \"{filePath}:{Math.Max(1, line)}:{Math.Max(1, column)}\"";
                }

                if (Application.platform == RuntimePlatform.OSXEditor && executablePath.EndsWith(".app"))
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = "/usr/bin/open";
                        // Add project root (current directory) as the first argument, followed by the file/line argument
                        string projectArg = $"\"{Directory.GetCurrentDirectory()}\"";
                        process.StartInfo.Arguments = $"-a \"{executablePath}\" --args {projectArg} {arguments}";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.WorkingDirectory = Directory.GetCurrentDirectory();
                        return process.Start();
                    }
                }
                else
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = executablePath;
                        // Add project root (current directory) as the first argument, followed by the file/line argument
                        string projectArg = $"\"{Directory.GetCurrentDirectory()}\"";
                        process.StartInfo.Arguments = $"{projectArg} {arguments}";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.WorkingDirectory = Directory.GetCurrentDirectory();
                        return process.Start();
                    }
                }
            }
            catch (Exception e)
            {
                return false;
            }
        }

        private string GetExecutablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                string executable = Path.Combine(path, "Contents", "MacOS", "Antigravity");
                return File.Exists(executable) ? executable : path;
            }

            return path;
        }

        public void SyncAll()
        {
            _projectGeneration.Sync();
        }

        public void SyncIfNeeded(string[] addedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, string[] importedAssets)
        {
            _projectGeneration.SyncIfNeeded(addedAssets.Union(deletedAssets).Union(movedAssets).Union(movedFromAssetPaths), importedAssets);
        }

        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            if (!string.IsNullOrEmpty(editorPath) &&
                editorPath.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                installation = new CodeEditor.Installation
                {
                    Name = EditorDisplayName,
                    Path = editorPath
                };
                return true;
            }

            installation = default;
            return false;
        }
    }
}
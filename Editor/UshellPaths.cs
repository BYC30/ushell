using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public static class UshellPaths
    {
        public static string ProjectPath => Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();

        public static string PackagePath
        {
            get
            {
                UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UshellPaths).Assembly);
                if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                {
                    return packageInfo.resolvedPath;
                }

                string[] guids = AssetDatabase.FindAssets("Ushell.Editor t:asmdef");
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (assetPath.EndsWith("Editor/Ushell.Editor.asmdef", StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetFullPath(Path.Combine(ProjectPath, Path.GetDirectoryName(assetPath), ".."));
                    }
                }

                return ProjectPath;
            }
        }

        public static string McpServerExecutablePath => Path.Combine(PackagePath, "Server", "publish", "Ushell.McpServer.exe");

        public static string ProjectKey => StableHash(ProjectPath);

        public static string BridgePipeName => "ushell-" + ProjectKey;

        public static string ResolveOutputPath(string requestedPath, string folderName, string defaultFileName)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(Path.IsPathRooted(requestedPath)
                    ? requestedPath
                    : Path.Combine(ProjectPath, requestedPath));
            }

            string directory = Path.Combine(ProjectPath, "UshellOutput", folderName);
            Directory.CreateDirectory(directory);
            string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{defaultFileName}";
            return Path.Combine(directory, fileName);
        }

        public static bool EnsureWriteAllowed(string outputPath, bool confirm, out string error)
        {
            outputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Directory.Exists(outputPath) ? outputPath : Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                error = "The output path is invalid.";
                return false;
            }

            UshellSettings settings = UshellSettings.Instance;
            string[] allowedPaths = settings.AllowedPaths;
            if (allowedPaths.Length == 0)
            {
                allowedPaths = new[]
                {
                    Path.Combine(ProjectPath, "UshellOutput"),
                    Path.Combine(ProjectPath, settings.DefaultBuildOutputRoot)
                };
            }

            bool matchesAllowedPath = allowedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeConfiguredPath)
                .Any(allowed => IsPathWithin(allowed, outputPath));

            if (!matchesAllowedPath)
            {
                error = $"Path '{outputPath}' is outside the configured allowed paths.";
                return false;
            }

            if ((File.Exists(outputPath) || Directory.Exists(outputPath)) && settings.DangerousOperationRequireConfirm && !confirm)
            {
                error = $"Path '{outputPath}' already exists and requires confirm=true.";
                return false;
            }

            Directory.CreateDirectory(outputDirectory);
            error = null;
            return true;
        }

        private static string NormalizeConfiguredPath(string path)
        {
            string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(ProjectPath, path);
            return Path.GetFullPath(fullPath);
        }

        private static bool IsPathWithin(string allowedPath, string candidatePath)
        {
            string allowed = Path.GetFullPath(allowedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(allowed, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string allowedPrefix = allowed + Path.DirectorySeparatorChar;
            return candidate.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string StableHash(string value)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(16);
                for (int index = 0; index < 8 && index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}

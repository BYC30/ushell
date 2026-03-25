using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public static class UshellPaths
    {
        public static string ProjectPath => Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();

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
                .Any(allowed => outputPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || outputDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));

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
    }
}

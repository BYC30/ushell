using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Ushell.Editor
{
    public sealed class BuildRequest
    {
        public string BuildProfile;
        public string Target;
        public bool Development;
        public string RequestedOutputPath;
        public bool Confirm;
    }

    public static class UshellBuildService
    {
        private static readonly object SyncRoot = new object();
        private static Dictionary<string, object> _lastBuildSummary = new Dictionary<string, object>
        {
            { "status", "never_built" }
        };

        public static Dictionary<string, object> GetLastBuildSummary()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, object>(_lastBuildSummary);
            }
        }

        public static UshellToolEnvelope Build(BuildRequest request)
        {
            BuildTarget target = ParseTarget(request.Target);
            string outputPath = ResolveBuildOutputPath(request, target);
            if (!UshellPaths.EnsureWriteAllowed(outputPath, request.Confirm, out string error))
            {
                return UshellToolEnvelope.FromError("UNAUTHORIZED_OPERATION", error);
            }

            string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
            {
                return UshellToolEnvelope.FromError("BUILD_FAILED", "No enabled scenes were found in EditorBuildSettings.");
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                target = target,
                locationPathName = outputPath,
                options = request.Development ? BuildOptions.Development : BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            Dictionary<string, object> summary = new Dictionary<string, object>
            {
                { "status", report.summary.result.ToString() },
                { "outputPath", outputPath },
                { "target", target.ToString() },
                { "totalErrors", report.summary.totalErrors },
                { "totalWarnings", report.summary.totalWarnings },
                { "totalSize", report.summary.totalSize },
                { "totalTimeSeconds", report.summary.totalTime.TotalSeconds }
            };

            lock (SyncRoot)
            {
                _lastBuildSummary = summary;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                return UshellToolEnvelope.FromError("BUILD_FAILED", "BuildPipeline reported a failed build.", summary);
            }

            UshellToolEnvelope envelope = UshellToolEnvelope.FromSuccess(summary);
            if (!string.IsNullOrWhiteSpace(request.BuildProfile))
            {
                envelope.Warnings.Add("buildProfile is accepted for forward compatibility but is not yet applied by the v1 build service.");
            }

            return envelope;
        }

        private static string ResolveBuildOutputPath(BuildRequest request, BuildTarget target)
        {
            string defaultFileName = GetDefaultFileName(target);
            if (!string.IsNullOrWhiteSpace(request.RequestedOutputPath))
            {
                return UshellPaths.ResolveOutputPath(request.RequestedOutputPath, "Builds", defaultFileName);
            }

            string relativePath = Path.Combine(UshellSettings.Instance.DefaultBuildOutputRoot, target.ToString(), defaultFileName);
            return UshellPaths.ResolveOutputPath(relativePath, "Builds", defaultFileName);
        }

        private static string GetDefaultFileName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "ushell-build.exe";
                default:
                    return "ushell-build";
            }
        }

        private static BuildTarget ParseTarget(string target)
        {
            if (!string.IsNullOrWhiteSpace(target) && Enum.TryParse(target, true, out BuildTarget parsed))
            {
                return parsed;
            }

            return BuildTarget.StandaloneWindows64;
        }
    }
}

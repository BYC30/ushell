using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    internal static class UshellSettingsProvider
    {
        [SettingsProvider]
        private static SettingsProvider Create()
        {
            SettingsProvider provider = new SettingsProvider("Project/Ushell", SettingsScope.Project)
            {
                label = "Ushell",
                guiHandler = _ => DrawGui()
            };

            return provider;
        }

        [MenuItem("Tools/Ushell/Open Settings")]
        private static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Ushell");
        }

        private static void DrawGui()
        {
            UshellSettings settings = UshellSettings.Instance;

            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            int port = EditorGUILayout.IntField("Port", settings.Port);
            if (port != settings.Port)
            {
                settings.Port = port;
                settings.SaveNow();
                UshellMcpServer.Restart();
            }

            EditorGUILayout.LabelField("Status", UshellMcpServer.GetStatusSummary());
            string lastError = UshellMcpServer.GetLastError();
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Execution", EditorStyles.boldLabel);
            bool requireConfirm = EditorGUILayout.Toggle("Require Confirm", settings.DangerousOperationRequireConfirm);
            if (requireConfirm != settings.DangerousOperationRequireConfirm)
            {
                settings.DangerousOperationRequireConfirm = requireConfirm;
                settings.SaveNow();
            }

            int maxExecutionSeconds = EditorGUILayout.IntField("Max Execution Seconds", settings.MaxExecutionSeconds);
            if (maxExecutionSeconds != settings.MaxExecutionSeconds)
            {
                settings.MaxExecutionSeconds = maxExecutionSeconds;
                settings.SaveNow();
            }

            string buildRoot = EditorGUILayout.TextField("Default Build Root", settings.DefaultBuildOutputRoot);
            if (buildRoot != settings.DefaultBuildOutputRoot)
            {
                settings.DefaultBuildOutputRoot = buildRoot;
                settings.SaveNow();
            }

            bool verboseLogs = EditorGUILayout.Toggle("Verbose Logs", settings.EnableVerboseLogs);
            if (verboseLogs != settings.EnableVerboseLogs)
            {
                settings.EnableVerboseLogs = verboseLogs;
                settings.SaveNow();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Allowed Paths", EditorStyles.boldLabel);
            List<string> allowedPaths = settings.AllowedPaths.ToList();
            int removeIndex = -1;
            bool pathsChanged = false;
            for (int index = 0; index < allowedPaths.Count; index++)
            {
                EditorGUILayout.BeginHorizontal();
                string updatedPath = EditorGUILayout.TextField(allowedPaths[index]);
                if (updatedPath != allowedPaths[index])
                {
                    allowedPaths[index] = updatedPath;
                    pathsChanged = true;
                }

                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    removeIndex = index;
                }

                EditorGUILayout.EndHorizontal();
            }

            if (removeIndex >= 0)
            {
                allowedPaths.RemoveAt(removeIndex);
                settings.AllowedPaths = allowedPaths.ToArray();
                settings.SaveNow();
            }
            else if (pathsChanged)
            {
                settings.AllowedPaths = allowedPaths.ToArray();
                settings.SaveNow();
            }

            if (GUILayout.Button("Add Allowed Path"))
            {
                allowedPaths.Add(string.Empty);
                settings.AllowedPaths = allowedPaths.ToArray();
                settings.SaveNow();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Registered Tools", EditorStyles.boldLabel);
            foreach (UshellToolDefinition tool in UshellToolRegistry.GetAll())
            {
                EditorGUILayout.LabelField(tool.Name);
            }
        }
    }
}

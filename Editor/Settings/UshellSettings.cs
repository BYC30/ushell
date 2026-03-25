using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ushell.Editor
{
    public sealed class UshellSettings
    {
        private const string KeyPrefix = "ushell.settings.";
        private const string PortKey = KeyPrefix + "port";
        private const string AllowedPathsKey = KeyPrefix + "allowedPaths";
        private const string DangerousConfirmKey = KeyPrefix + "dangerousConfirm";
        private const string MaxExecutionSecondsKey = KeyPrefix + "maxExecutionSeconds";
        private const string DefaultBuildOutputRootKey = KeyPrefix + "defaultBuildOutputRoot";
        private const string EnableVerboseLogsKey = KeyPrefix + "enableVerboseLogs";

        private static readonly Lazy<UshellSettings> LazyInstance = new Lazy<UshellSettings>(Load);

        private int _port;
        private string[] _allowedPaths;
        private bool _dangerousOperationRequireConfirm;
        private int _maxExecutionSeconds;
        private string _defaultBuildOutputRoot;
        private bool _enableVerboseLogs;

        public static UshellSettings Instance => LazyInstance.Value;

        public int Port
        {
            get => _port;
            set => _port = Mathf.Clamp(value, 1024, 65535);
        }

        public string[] AllowedPaths
        {
            get => _allowedPaths ?? new string[0];
            set => _allowedPaths = value ?? new string[0];
        }

        public bool DangerousOperationRequireConfirm
        {
            get => _dangerousOperationRequireConfirm;
            set => _dangerousOperationRequireConfirm = value;
        }

        public int MaxExecutionSeconds
        {
            get => Mathf.Clamp(_maxExecutionSeconds, 1, 300);
            set => _maxExecutionSeconds = Mathf.Clamp(value, 1, 300);
        }

        public string DefaultBuildOutputRoot
        {
            get => string.IsNullOrWhiteSpace(_defaultBuildOutputRoot) ? "UshellOutput/Builds" : _defaultBuildOutputRoot;
            set => _defaultBuildOutputRoot = value;
        }

        public bool EnableVerboseLogs
        {
            get => _enableVerboseLogs;
            set => _enableVerboseLogs = value;
        }

        public void SaveNow()
        {
            EditorPrefs.SetInt(PortKey, Port);
            EditorPrefs.SetString(AllowedPathsKey, string.Join("\n", AllowedPaths.Where(path => path != null).ToArray()));
            EditorPrefs.SetBool(DangerousConfirmKey, DangerousOperationRequireConfirm);
            EditorPrefs.SetInt(MaxExecutionSecondsKey, MaxExecutionSeconds);
            EditorPrefs.SetString(DefaultBuildOutputRootKey, DefaultBuildOutputRoot ?? "UshellOutput/Builds");
            EditorPrefs.SetBool(EnableVerboseLogsKey, EnableVerboseLogs);
        }

        private static UshellSettings Load()
        {
            return new UshellSettings
            {
                _port = EditorPrefs.GetInt(PortKey, 61337),
                _allowedPaths = SplitPaths(EditorPrefs.GetString(AllowedPathsKey, string.Empty)),
                _dangerousOperationRequireConfirm = EditorPrefs.GetBool(DangerousConfirmKey, true),
                _maxExecutionSeconds = EditorPrefs.GetInt(MaxExecutionSecondsKey, 15),
                _defaultBuildOutputRoot = EditorPrefs.GetString(DefaultBuildOutputRootKey, "UshellOutput/Builds"),
                _enableVerboseLogs = EditorPrefs.GetBool(EnableVerboseLogsKey, true)
            };
        }

        private static string[] SplitPaths(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new string[0];
            }

            return raw
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }
    }
}

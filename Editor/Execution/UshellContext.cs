using System.Collections.Generic;
using System.Threading;
using UnityEditor;

namespace Ushell.Editor
{
    public sealed class UshellContext
    {
        private readonly List<Dictionary<string, object>> _capturedLogs = new List<Dictionary<string, object>>();

        public UshellContext(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public IReadOnlyList<Dictionary<string, object>> CapturedLogs => _capturedLogs;

        public void Log(string message)
        {
            AddLog("Info", message);
            UnityEngine.Debug.Log($"[ushell] {message}");
        }

        public void AddLog(string type, string message)
        {
            _capturedLogs.Add(new Dictionary<string, object>
            {
                { "type", type },
                { "message", message }
            });
        }

        public string[] SelectionAssetPaths()
        {
            List<string> result = new List<string>();
            foreach (UnityEngine.Object selectedObject in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selectedObject);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(path);
                }
            }

            return result.ToArray();
        }
    }
}

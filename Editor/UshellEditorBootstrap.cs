using UnityEditor;

namespace Ushell.Editor
{
    [InitializeOnLoad]
    public static class UshellEditorBootstrap
    {
        static UshellEditorBootstrap()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.delayCall += StartServer;
        }

        private static void OnBeforeAssemblyReload()
        {
            UshellMcpServer.Stop();
        }

        private static void OnAfterAssemblyReload()
        {
            EditorApplication.delayCall += StartServer;
        }

        private static void OnQuitting()
        {
            UshellMcpServer.Stop();
        }

        private static void StartServer()
        {
            if (!EditorApplication.isCompiling)
            {
                UshellMcpServer.Start();
            }
        }
    }
}

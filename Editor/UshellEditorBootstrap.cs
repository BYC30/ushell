using UnityEditor;

namespace Ushell.Editor
{
    [InitializeOnLoad]
    public static class UshellEditorBootstrap
    {
        private static bool _startPending;

        static UshellEditorBootstrap()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnQuitting;
            RequestStart();
        }

        private static void OnBeforeAssemblyReload()
        {
            StopBridgeForDomainTransition("beforeAssemblyReload");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                StopBridgeForDomainTransition("playModeStateChanged:" + state);
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
            {
                RequestStart();
            }
        }

        private static void StopBridgeForDomainTransition(string reason)
        {
            UshellEditorBridgeServer.Stop(reason);
        }

        private static void OnAfterAssemblyReload()
        {
            RequestStart();
        }

        private static void OnQuitting()
        {
            UshellEditorBridgeServer.Stop("editorQuitting");
            UshellMcpProcessSupervisor.Stop();
        }

        private static void StartServices()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                RequestStart();
                return;
            }

            _startPending = false;
            EditorApplication.update -= StartServicesWhenReady;
            UshellEditorDispatcher.EnsureInitialized();
            UshellEditorBridgeServer.Start();
            UshellMcpProcessSupervisor.EnsureStarted();
        }

        private static void RequestStart()
        {
            if (_startPending)
            {
                return;
            }

            _startPending = true;
            EditorApplication.update -= StartServicesWhenReady;
            EditorApplication.update += StartServicesWhenReady;
            EditorApplication.delayCall += StartServices;
        }

        private static void StartServicesWhenReady()
        {
            if (!_startPending)
            {
                EditorApplication.update -= StartServicesWhenReady;
                return;
            }

            StartServices();
        }
    }
}

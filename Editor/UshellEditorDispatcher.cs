using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace Ushell.Editor
{
    [InitializeOnLoad]
    public static class UshellEditorDispatcher
    {
        private static readonly Queue<Action> PendingActions = new Queue<Action>();
        private static readonly object SyncRoot = new object();

        static UshellEditorDispatcher()
        {
            EditorApplication.update += Drain;
        }

        public static Task<T> InvokeAsync<T>(Func<T> func)
        {
            TaskCompletionSource<T> completionSource = new TaskCompletionSource<T>();
            lock (SyncRoot)
            {
                PendingActions.Enqueue(() =>
                {
                    try
                    {
                        completionSource.SetResult(func());
                    }
                    catch (Exception exception)
                    {
                        completionSource.SetException(exception);
                    }
                });
            }

            return completionSource.Task;
        }

        private static void Drain()
        {
            while (true)
            {
                Action action;
                lock (SyncRoot)
                {
                    if (PendingActions.Count == 0)
                    {
                        return;
                    }

                    action = PendingActions.Dequeue();
                }

                action();
            }
        }
    }
}

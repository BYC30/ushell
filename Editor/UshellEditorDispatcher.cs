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

        public static Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            TaskCompletionSource<T> completionSource = new TaskCompletionSource<T>();
            lock (SyncRoot)
            {
                PendingActions.Enqueue(() =>
                {
                    try
                    {
                        Task<T> task = func();
                        if (task == null)
                        {
                            completionSource.SetException(new InvalidOperationException("Dispatcher function returned null task."));
                            return;
                        }

                        task.ContinueWith(completedTask =>
                        {
                            if (completedTask.IsCanceled)
                            {
                                completionSource.SetCanceled();
                            }
                            else if (completedTask.IsFaulted)
                            {
                                AggregateException aggregateException = completedTask.Exception;
                                if (aggregateException != null)
                                {
                                    completionSource.SetException(aggregateException.InnerExceptions);
                                }
                                else
                                {
                                    completionSource.SetException(new InvalidOperationException("Task faulted without aggregate exception."));
                                }
                            }
                            else
                            {
                                completionSource.SetResult(completedTask.Result);
                            }
                        });
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

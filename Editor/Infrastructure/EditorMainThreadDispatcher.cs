using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    internal sealed class EditorMainThreadDispatcher : IDisposable
    {
        private readonly ConcurrentQueue<Func<Task>> work = new ConcurrentQueue<Func<Task>>();
        private bool disposed;

        public EditorMainThreadDispatcher()
        {
            EditorApplication.update += Drain;
        }

        public Task Enqueue(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return Enqueue(() =>
            {
                action();
                return Task.CompletedTask;
            });
        }

        public Task Enqueue(Func<Task> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (disposed)
            {
                throw new ObjectDisposedException(nameof(EditorMainThreadDispatcher));
            }

            var completion = new TaskCompletionSource<object>();
            work.Enqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                    throw;
                }
            });
            return completion.Task;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            EditorApplication.update -= Drain;
        }

        private void Drain()
        {
            while (work.TryDequeue(out var action))
            {
                Observe(action());
            }
        }

        private static async void Observe(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}

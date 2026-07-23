using System;
using System.Diagnostics;

namespace VeryFS.UnityMCP.Editor.UI
{
    public readonly struct ProcessIdentity
    {
        public ProcessIdentity(int pid, string name, string mainModulePath)
        {
            Pid = pid;
            Name = name;
            MainModulePath = mainModulePath;
        }

        public int Pid { get; }
        public string Name { get; }
        public string MainModulePath { get; }
    }

    /// <summary>Process lookup/kill seam so ServerKiller is testable without
    /// touching real OS processes.</summary>
    public interface IProcessController
    {
        /// <summary>Identity of the process with this pid, or null if none exists.</summary>
        ProcessIdentity? Find(int pid);

        void Kill(int pid);
    }

    public sealed class SystemProcessController : IProcessController
    {
        public ProcessIdentity? Find(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                string modulePath = null;
                try
                {
                    modulePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // MainModule can throw (access denied / exited). Degrade to
                    // name-only identity; ServerKiller still verifies by name.
                }

                return new ProcessIdentity(pid, process.ProcessName, modulePath);
            }
            catch (Exception)
            {
                // ArgumentException => no such process. InvalidOperationException
                // => already exited. Either way: nothing to identify.
                return null;
            }
        }

        public void Kill(int pid)
        {
            Process.GetProcessById(pid).Kill();
        }
    }
}

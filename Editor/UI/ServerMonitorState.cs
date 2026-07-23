namespace VeryFS.UnityMCP.Editor.UI
{
    public enum MonitorStatus
    {
        Unknown,
        Connected,
        ServerUpNoUnity,
        Checking,
        Unresponsive
    }

    /// <summary>
    /// Pure state machine for the monitor window. Ingests poll outcomes and
    /// derives a display status. A poll failure only becomes "Unresponsive"
    /// after UnresponsiveThreshold consecutive failures, so a single transient
    /// hiccup (compile/GC/busy main thread) shows as "Checking" instead.
    /// </summary>
    public sealed class ServerMonitorState
    {
        public const int UnresponsiveThreshold = 2;

        public MonitorStatus Status { get; private set; } = MonitorStatus.Unknown;
        public int ConsecutiveFailures { get; private set; }
        public bool HasSnapshot { get; private set; }
        public ServerHealthSnapshot Last { get; private set; }

        public void Observe(ServerHealthSnapshot snapshot)
        {
            Last = snapshot;
            HasSnapshot = true;
            ConsecutiveFailures = 0;
            Status = snapshot.UnityConnected ? MonitorStatus.Connected : MonitorStatus.ServerUpNoUnity;
        }

        public void ObserveFailure()
        {
            ConsecutiveFailures++;
            Status = ConsecutiveFailures >= UnresponsiveThreshold
                ? MonitorStatus.Unresponsive
                : MonitorStatus.Checking;
        }
    }
}

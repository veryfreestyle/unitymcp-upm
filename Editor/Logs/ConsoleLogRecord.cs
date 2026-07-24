namespace VeryFS.UnityMCP.Editor.Logs
{
    // A single Unity console entry as read from the native LogEntries buffer.
    // Unlike the old managed-callback capture, this has no timestamp: the native
    // LogEntry carries none, so time-based filtering is not available for this source.
    public sealed class ConsoleLogRecord
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string StackTrace { get; set; }
    }
}

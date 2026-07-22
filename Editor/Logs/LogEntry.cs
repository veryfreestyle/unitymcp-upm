namespace VeryFS.UnityMCP.Editor.Logs
{
    public sealed class LogEntry
    {
        public string TimestampUtc { get; set; }
        public string Type { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
    }
}

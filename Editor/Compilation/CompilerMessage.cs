namespace VeryFS.UnityMCP.Editor.Compilation
{
    public sealed class CompilerMessage
    {
        public CompilerMessage()
        {
        }

        public CompilerMessage(string assembly, string file, int line, int column, string message, bool isError)
        {
            Assembly = assembly;
            File = file;
            Line = line;
            Column = column;
            Message = message;
            IsError = isError;
        }

        public string Assembly { get; set; }

        public string File { get; set; }

        public int Line { get; set; }

        public int Column { get; set; }

        public string Message { get; set; }

        public bool IsError { get; set; }
    }
}

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public static class JsonRpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        public const int EditorBusy = -32010;
        public const int CompilationFailed = -32004;
        public const int RequestTimeout = -32006;
        public const int InvalidEditorState = -32007;
        public const int RegistrationFailed = -32009;
    }
}

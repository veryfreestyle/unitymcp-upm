using LitJson;

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public sealed class JsonRpcError
    {
        public JsonRpcError(int code, string message, JsonData data)
        {
            Code = code;
            Message = message;
            Data = data;
        }

        public int Code { get; }

        public string Message { get; }

        public JsonData Data { get; }
    }
}

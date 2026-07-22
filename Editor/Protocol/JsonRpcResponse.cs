using LitJson;

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public sealed class JsonRpcResponse
    {
        private JsonRpcResponse(JsonRpcRequest request, string id, JsonData result, JsonRpcError error)
        {
            Request = request;
            Id = id;
            Result = result;
            Error = error;
        }

        public JsonRpcRequest Request { get; }

        public string Id { get; }

        public JsonData Result { get; }

        public JsonRpcError Error { get; }

        public static JsonRpcResponse FromRequest(JsonRpcRequest request)
        {
            return new JsonRpcResponse(request, null, null, null);
        }

        public static JsonRpcResponse FromSuccess(string id, JsonData result)
        {
            return new JsonRpcResponse(null, id, result, null);
        }

        public static JsonRpcResponse FromError(string id, JsonRpcError error)
        {
            return new JsonRpcResponse(null, id, null, error);
        }
    }
}

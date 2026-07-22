using LitJson;

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public sealed class JsonRpcRequest
    {
        public JsonRpcRequest(string id, string method, JsonData @params)
        {
            Id = id;
            Method = method;
            Params = @params;
        }

        public string Id { get; }

        public string Method { get; }

        public JsonData Params { get; }

        public static JsonRpcRequest Create(string id, string method, JsonData @params)
        {
            return new JsonRpcRequest(id, method, @params);
        }
    }
}

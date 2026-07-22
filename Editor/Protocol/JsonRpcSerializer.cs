using System;
using System.Collections;
using LitJson;

namespace VeryFS.UnityMCP.Editor.Protocol
{
    public static class JsonRpcSerializer
    {
        public static JsonRpcResponse Parse(string json)
        {
            if (json != null && json.Trim() == "null")
            {
                throw InvalidRequest("JSON-RPC message must be an object.");
            }

            JsonData message;
            try
            {
                message = JsonMapper.ToObject(json);
            }
            catch (Exception ex)
            {
                throw new RpcProtocolException(JsonRpcErrorCodes.ParseError, "Invalid JSON-RPC JSON.", ex);
            }

            if (message == null || !message.IsObject || !HasStringValue(message, JsonRpcConstants.VersionProperty, JsonRpcConstants.Version))
            {
                throw InvalidRequest("The JSON-RPC version must be 2.0.");
            }

            if (message.ContainsKey(JsonRpcConstants.MethodProperty))
            {
                return JsonRpcResponse.FromRequest(ParseRequest(message));
            }

            return ParseResponse(message);
        }

        public static string SerializeRequest(string id, string method, JsonData @params)
        {
            ValidateId(id);

            var message = Object(
                (JsonRpcConstants.VersionProperty, JsonRpcConstants.Version),
                (JsonRpcConstants.IdProperty, id),
                (JsonRpcConstants.MethodProperty, method),
                (JsonRpcConstants.ParamsProperty, @params));

            return JsonMapper.ToJson(message);
        }

        public static string SerializeSuccess(string id, JsonData result)
        {
            ValidateId(id);

            return JsonMapper.ToJson(Object(
                (JsonRpcConstants.VersionProperty, JsonRpcConstants.Version),
                (JsonRpcConstants.IdProperty, id),
                (JsonRpcConstants.ResultProperty, result)));
        }

        public static string SerializeError(string id, int code, string message, string errorCode, string activeRequestId)
        {
            var data = Object(("errorCode", errorCode));
            if (!string.IsNullOrEmpty(activeRequestId))
            {
                data["activeRequestId"] = activeRequestId;
            }

            return SerializeError(id, code, message, data);
        }

        public static string SerializeError(string id, int code, string message, JsonData data)
        {
            var error = Object(
                (JsonRpcConstants.CodeProperty, code),
                (JsonRpcConstants.MessageProperty, message));
            if (data != null)
            {
                error[JsonRpcConstants.DataProperty] = data;
            }

            return JsonMapper.ToJson(Object(
                (JsonRpcConstants.VersionProperty, JsonRpcConstants.Version),
                (JsonRpcConstants.IdProperty, id),
                (JsonRpcConstants.ErrorProperty, error)));
        }

        public static JsonData Object(params (string Name, object Value)[] properties)
        {
            var data = new JsonData();
            data.SetJsonType(JsonType.Object);

            foreach (var property in properties)
            {
                ((IDictionary)data)[property.Name] = property.Value;
            }

            return data;
        }

        public static string TryGetStringId(string json)
        {
            try
            {
                var message = JsonMapper.ToObject(json);
                return message != null && message.IsObject && HasStringValue(message, JsonRpcConstants.IdProperty)
                    ? (string)message[JsonRpcConstants.IdProperty]
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static JsonRpcRequest ParseRequest(JsonData message)
        {
            if (!HasStringValue(message, JsonRpcConstants.IdProperty) || !HasStringValue(message, JsonRpcConstants.MethodProperty))
            {
                throw InvalidRequest("JSON-RPC requests require string id and method values.");
            }

            var @params = message.ContainsKey(JsonRpcConstants.ParamsProperty)
                ? message[JsonRpcConstants.ParamsProperty]
                : null;
            return new JsonRpcRequest(
                (string)message[JsonRpcConstants.IdProperty],
                (string)message[JsonRpcConstants.MethodProperty],
                @params);
        }

        private static JsonRpcResponse ParseResponse(JsonData message)
        {
            if (!HasStringValue(message, JsonRpcConstants.IdProperty))
            {
                throw InvalidRequest("JSON-RPC responses require a string id.");
            }

            var hasResult = message.ContainsKey(JsonRpcConstants.ResultProperty);
            var hasError = message.ContainsKey(JsonRpcConstants.ErrorProperty);
            if (hasResult == hasError)
            {
                throw InvalidRequest("JSON-RPC responses require exactly one result or error.");
            }

            var id = (string)message[JsonRpcConstants.IdProperty];
            if (hasResult)
            {
                return JsonRpcResponse.FromSuccess(id, message[JsonRpcConstants.ResultProperty]);
            }

            var errorData = message[JsonRpcConstants.ErrorProperty];
            if (errorData == null || !errorData.IsObject || !errorData.ContainsKey(JsonRpcConstants.CodeProperty) ||
                !errorData[JsonRpcConstants.CodeProperty].IsInt ||
                !HasStringValue(errorData, JsonRpcConstants.MessageProperty))
            {
                throw InvalidRequest("JSON-RPC errors require integer code and string message values.");
            }

            var data = errorData.ContainsKey(JsonRpcConstants.DataProperty)
                ? errorData[JsonRpcConstants.DataProperty]
                : null;
            return JsonRpcResponse.FromError(id, new JsonRpcError(
                (int)errorData[JsonRpcConstants.CodeProperty],
                (string)errorData[JsonRpcConstants.MessageProperty],
                data));
        }

        private static bool HasStringValue(JsonData data, string property, string expectedValue = null)
        {
            if (data == null || !data.IsObject || !data.ContainsKey(property) ||
                data[property] == null || !data[property].IsString)
            {
                return false;
            }

            return expectedValue == null || (string)data[property] == expectedValue;
        }

        private static RpcProtocolException InvalidRequest(string message)
        {
            return new RpcProtocolException(JsonRpcErrorCodes.InvalidRequest, message);
        }

        private static void ValidateId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("JSON-RPC id must be a non-empty string.", nameof(id));
            }
        }
    }
}

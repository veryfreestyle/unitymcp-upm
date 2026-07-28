using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    // asset 组四个子命令共用的入参读取。LitJson 的 JsonData 没有类型安全访问器,
    // 统一在这里做 IsXxx 判断, 避免四份重复。
    internal static class AssetParams
    {
        public static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;

        public static bool ReadBool(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean && (bool)p[key];

        public static int? ReadInt(JsonData p, string key)
        {
            if (p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt)
            {
                return (int)p[key];
            }
            return null;
        }

        public static List<string> ReadStringArray(JsonData p, string key)
        {
            if (p == null || !p.IsObject || !p.ContainsKey(key) || !p[key].IsArray)
            {
                return null;
            }
            var list = new List<string>();
            JsonData array = p[key];
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i].IsString) { list.Add((string)array[i]); }
            }
            return list;
        }
    }

    // asset 组共用的响应构造。错误语义见 spec 第 4 节。
    internal static class AssetResponses
    {
        public static JsonRpcResponse Busy(string id)
            => JsonRpcResponse.FromSuccess(id,
                JsonRpcSerializer.Object(("state", "editor_busy"), ("truncated", false)));

        public static JsonRpcResponse NotFound(string id)
            => JsonRpcResponse.FromSuccess(id,
                JsonRpcSerializer.Object(("state", "not_found"), ("truncated", false)));

        public static JsonRpcResponse ComponentNotFound(string id)
            => JsonRpcResponse.FromSuccess(id,
                JsonRpcSerializer.Object(("state", "component_not_found"), ("truncated", false)));

        public static JsonRpcResponse InvalidParams(string id, string message)
            => Error(id, message, "invalid_params");

        public static JsonRpcResponse AssetNotFound(string id, string message)
            => Error(id, message, "asset_not_found");

        public static JsonRpcResponse UnsupportedAssetType(string id, string message)
            => Error(id, message, "unsupported_asset_type");

        // JsonRpcErrorCodes 没有「对象不存在 / 类型不支持」的专用数值码, 三者都用
        // InvalidParams: 本质都是入参指向的东西不存在或不支持。判别靠 errorCode 字符串。
        private static JsonRpcResponse Error(string id, string message, string errorCode)
            => JsonRpcResponse.FromError(id, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams, message,
                JsonRpcSerializer.Object(("errorCode", errorCode))));
    }

    // find / component-get 共用的定位链: path/guid -> 主对象必须是 GameObject -> childPath。
    // 失败时直接给出成型的响应, 调用方原样返回。
    internal static class AssetTargetLookup
    {
        public static bool TryLocateNode(IAssetGateway gateway, JsonRpcRequest request,
            out UnityEngine.GameObject node, out JsonRpcResponse failure)
        {
            node = null;
            failure = null;

            string path = AssetParams.ReadString(request.Params, "path");
            string guid = AssetParams.ReadString(request.Params, "guid");
            if (!AssetTargetResolver.TryResolve(gateway, path, guid,
                    out AssetTarget target, out string errorCode, out string error))
            {
                failure = errorCode == "asset_not_found"
                    ? AssetResponses.AssetNotFound(request.Id, error)
                    : AssetResponses.InvalidParams(request.Id, error);
                return false;
            }

            var root = target.MainObject as UnityEngine.GameObject;
            if (root == null)
            {
                failure = AssetResponses.UnsupportedAssetType(request.Id,
                    "main object of " + target.Path + " is not a GameObject; " +
                    "find/component-get only support prefabs and imported models");
                return false;
            }

            string childPath = AssetParams.ReadString(request.Params, "childPath");
            if (string.IsNullOrEmpty(childPath))
            {
                node = root;
                return true;
            }

            UnityEngine.Transform child = root.transform.Find(childPath);
            if (child == null)
            {
                // 探索性失败 (路径拼错 / 对树结构的假设有误), 不是异常, 对齐 gameobject.find。
                failure = AssetResponses.NotFound(request.Id);
                return false;
            }

            node = child.gameObject;
            return true;
        }
    }
}

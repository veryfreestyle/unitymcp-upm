namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    // 解析结果: 规范化后的 path + guid + 主对象 (可能为 null, 例如目录)。
    public readonly struct AssetTarget
    {
        public AssetTarget(string path, string guid, UnityEngine.Object mainObject)
        {
            Path = path;
            Guid = guid;
            MainObject = mainObject;
        }

        public string Path { get; }
        public string Guid { get; }
        public UnityEngine.Object MainObject { get; }
    }

    // path / guid 二选一定位。两个都传时以 guid 为准 —— .mat 等序列化文件里存的就是
    // guid, 排查断裂引用必须能按 guid 反查。不做 path 与 guid 的交叉一致性校验。
    public static class AssetTargetResolver
    {
        public const string MissingLocatorError = "path or guid required";

        public static bool TryResolve(IAssetGateway gateway, string path, string guid,
            out AssetTarget target, out string errorCode, out string error)
        {
            target = default;
            errorCode = null;
            error = null;

            string resolvedPath;
            if (!string.IsNullOrEmpty(guid))
            {
                resolvedPath = gateway.GuidToPath(guid);
                if (string.IsNullOrEmpty(resolvedPath))
                {
                    errorCode = "asset_not_found";
                    error = "no asset for guid " + guid;
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(path))
            {
                resolvedPath = path;
            }
            else
            {
                errorCode = "invalid_params";
                error = MissingLocatorError;
                return false;
            }

            string resolvedGuid = gateway.PathToGuid(resolvedPath);
            if (string.IsNullOrEmpty(resolvedGuid))
            {
                errorCode = "asset_not_found";
                error = "no asset at path " + resolvedPath;
                return false;
            }

            target = new AssetTarget(resolvedPath, resolvedGuid, gateway.LoadMainAsset(resolvedPath));
            return true;
        }
    }
}

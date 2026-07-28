using System.Collections.Generic;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    // AssetDatabase 接缝: asset 组的四个子命令只经这层查资源。
    // 单测用 FakeAssetGateway 驱动, 不依赖仓库里的真实资源。
    public interface IAssetGateway
    {
        // filter 为 Unity 搜索语法; searchInFolders 为 null 或空时搜全库。
        IReadOnlyList<string> FindAssets(string filter, string[] searchInFolders);

        string GuidToPath(string guid);

        string PathToGuid(string path);

        UnityEngine.Object LoadMainAsset(string path);

        // 主对象之外的子资源 (fbx 里的 mesh/animation、图集里的 sprite 等)。
        IReadOnlyList<UnityEngine.Object> LoadAllRepresentations(string path);

        // 只问导入器要类型名, 不加载对象 —— search 逐条取类型时不能加载整个资源。
        string GetMainAssetTypeName(string path);

        string GetImporterTypeName(string path);

        bool IsValidFolder(string path);
    }
}

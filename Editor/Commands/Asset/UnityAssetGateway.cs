using System;
using System.Collections.Generic;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    public sealed class UnityAssetGateway : IAssetGateway
    {
        public IReadOnlyList<string> FindAssets(string filter, string[] searchInFolders)
            => searchInFolders == null || searchInFolders.Length == 0
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, searchInFolders);

        public string GuidToPath(string guid) => AssetDatabase.GUIDToAssetPath(guid);

        public string PathToGuid(string path) => AssetDatabase.AssetPathToGUID(path);

        public UnityEngine.Object LoadMainAsset(string path) => AssetDatabase.LoadMainAssetAtPath(path);

        public IReadOnlyList<UnityEngine.Object> LoadAllRepresentations(string path)
            => AssetDatabase.LoadAllAssetRepresentationsAtPath(path);

        public string GetMainAssetTypeName(string path)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type == null ? null : type.Name;
        }

        public string GetImporterTypeName(string path)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            return importer == null ? null : importer.GetType().FullName;
        }

        public bool IsValidFolder(string path) => AssetDatabase.IsValidFolder(path);
    }
}

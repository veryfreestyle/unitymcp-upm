using System;
using System.IO;
using UnityEditor;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.AgentSkill
{
    internal interface IAgentSkillTemplateReader
    {
        string Read();
    }

    internal interface IUnityAssetPathResolver
    {
        string GuidToAssetPath(string guid);
        string PackageRootForAssetPath(string assetPath);
    }

    internal interface IAgentSkillTextFile
    {
        bool Exists(string path);
        string ReadAllText(string path);
    }

    internal sealed class GuidAgentSkillTemplateReader : IAgentSkillTemplateReader
    {
        public const string TemplateGuid = "d8631d9125f44e6aa6d23eaab67b6b21";

        private readonly string projectRoot;
        private readonly IUnityAssetPathResolver resolver;
        private readonly IAgentSkillTextFile files;

        public GuidAgentSkillTemplateReader(string projectRoot, IUnityAssetPathResolver resolver, IAgentSkillTextFile files)
        {
            this.projectRoot = projectRoot;
            this.resolver = resolver;
            this.files = files;
        }

        public string Read()
        {
            string assetPath = resolver.GuidToAssetPath(TemplateGuid);
            if (string.IsNullOrEmpty(assetPath))
            {
                TemplateMissing(null);
            }

            string path = ResolveFilePath(assetPath);
            if (!files.Exists(path))
            {
                TemplateMissing(path);
            }

            try
            {
                return files.ReadAllText(path);
            }
            catch (IOException)
            {
                TemplateReadFailed(path);
            }
            catch (UnauthorizedAccessException)
            {
                TemplateReadFailed(path);
            }

            throw new InvalidOperationException();
        }

        private string ResolveFilePath(string assetPath)
        {
            if (Path.IsPathRooted(assetPath))
            {
                return Path.GetFullPath(assetPath);
            }

            string packagePath = ResolvePackageFilePath(assetPath);
            return packagePath ?? Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private string ResolvePackageFilePath(string assetPath)
        {
            string normalizedAssetPath = assetPath.Replace('\\', '/');
            const string prefix = "Packages/";
            if (!normalizedAssetPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            string packageRoot = resolver.PackageRootForAssetPath(assetPath);
            if (string.IsNullOrEmpty(packageRoot))
            {
                return null;
            }

            string packageRelativePath = normalizedAssetPath.Substring(prefix.Length);
            int separator = packageRelativePath.IndexOf('/');
            if (separator < 0)
            {
                return Path.GetFullPath(packageRoot);
            }

            string pathWithinPackage = packageRelativePath.Substring(separator + 1)
                .Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(packageRoot, pathWithinPackage));
        }

        private static void TemplateMissing(string path)
        {
            throw new AgentSkillOperationException(JsonRpcErrorCodes.InvalidEditorState, "skill_template_missing",
                "The bundled agent skill template is missing.", path: path);
        }

        private static void TemplateReadFailed(string path)
        {
            throw new AgentSkillOperationException(JsonRpcErrorCodes.InvalidEditorState, "skill_template_read_failed",
                "The bundled agent skill template could not be read.", path: path);
        }
    }

    internal sealed class UnityAssetPathResolver : IUnityAssetPathResolver
    {
        public string GuidToAssetPath(string guid)
        {
            return AssetDatabase.GUIDToAssetPath(guid);
        }

        public string PackageRootForAssetPath(string assetPath)
        {
            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return packageInfo?.resolvedPath;
        }
    }

    internal sealed class SystemAgentSkillTextFile : IAgentSkillTextFile
    {
        public bool Exists(string path)
        {
            return File.Exists(path);
        }

        public string ReadAllText(string path)
        {
            return File.ReadAllText(path);
        }
    }
}

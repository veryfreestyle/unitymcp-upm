using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    public sealed class UnityGameViewEnvironment : IGameViewEnvironment
    {
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Type gameViewType;
        private readonly Type gameViewSizesType;
        private readonly PropertyInfo selectedSizeIndexProperty;
        private readonly FieldInfo renderTextureField;
        private readonly object gameViewSizes;
        private readonly PropertyInfo currentGroupProperty;
        private readonly MethodInfo getTotalCountMethod;
        private readonly MethodInfo getDisplayTextsMethod;
        private readonly MethodInfo getGameViewSizeMethod;
        private readonly PropertyInfo sizeTypeProperty;
        private readonly PropertyInfo widthProperty;
        private readonly PropertyInfo heightProperty;

        public UnityGameViewEnvironment()
        {
            var editorAssembly = typeof(UnityEditor.Editor).Assembly;
            gameViewType = RequiredType(editorAssembly, "UnityEditor.GameView");
            gameViewSizesType = RequiredType(editorAssembly, "UnityEditor.GameViewSizes");
            var gameViewSizeGroupType = RequiredType(editorAssembly, "UnityEditor.GameViewSizeGroup");
            var gameViewSizeType = RequiredType(editorAssembly, "UnityEditor.GameViewSize");

            selectedSizeIndexProperty = RequiredProperty(
                gameViewType, "selectedSizeIndex", InstanceMembers);
            renderTextureField = RequiredField(
                gameViewType, "m_RenderTexture", InstanceMembers);

            var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
            var singletonProperty = RequiredProperty(
                singletonType, "instance", BindingFlags.Static | BindingFlags.Public);
            gameViewSizes = singletonProperty.GetValue(null, null);
            currentGroupProperty = RequiredProperty(
                gameViewSizesType, "currentGroup", InstanceMembers);

            getTotalCountMethod = RequiredMethod(
                gameViewSizeGroupType, "GetTotalCount", Type.EmptyTypes);
            getDisplayTextsMethod = RequiredMethod(
                gameViewSizeGroupType, "GetDisplayTexts", Type.EmptyTypes);
            getGameViewSizeMethod = RequiredMethod(
                gameViewSizeGroupType, "GetGameViewSize", new[] { typeof(int) });

            sizeTypeProperty = RequiredProperty(gameViewSizeType, "sizeType", InstanceMembers);
            widthProperty = RequiredProperty(gameViewSizeType, "width", InstanceMembers);
            heightProperty = RequiredProperty(gameViewSizeType, "height", InstanceMembers);
        }

        public GameViewTargetResult FindTarget()
        {
            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(gameViewType);
            var windows = new List<EditorWindow>(objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is EditorWindow window)
                {
                    windows.Add(window);
                }
            }

            var selection = GameViewWindowSelector.Select(windows, EditorWindow.focusedWindow);
            return selection.Ok
                ? GameViewTargetResult.Success(new UnityGameViewTarget(
                    selection.Window, selectedSizeIndexProperty, renderTextureField))
                : GameViewTargetResult.Failure(selection.ErrorCode, selection.Error);
        }

        public IReadOnlyList<GameViewResolutionInfo> ListResolutions()
        {
            object group = currentGroupProperty.GetValue(gameViewSizes, null);
            int count = (int)getTotalCountMethod.Invoke(group, null);
            var displayTexts = (string[])getDisplayTextsMethod.Invoke(group, null);
            var result = new List<GameViewResolutionInfo>(count);

            for (int i = 0; i < count; i++)
            {
                object size = getGameViewSizeMethod.Invoke(group, new object[] { i });
                string sizeType = sizeTypeProperty.GetValue(size, null).ToString();
                string mode = sizeType == "FixedResolution" ? "fixed" : "aspect";
                int width = (int)widthProperty.GetValue(size, null);
                int height = (int)heightProperty.GetValue(size, null);
                string name = i < displayTexts.Length ? displayTexts[i] : string.Empty;
                result.Add(new GameViewResolutionInfo(i, name, mode, width, height));
            }

            return result;
        }

        private static Type RequiredType(Assembly assembly, string name)
            => assembly.GetType(name) ??
               throw new MissingMemberException("Unity Editor type not found: " + name);

        private static PropertyInfo RequiredProperty(Type type, string name, BindingFlags flags)
            => type.GetProperty(name, flags) ??
               throw new MissingMemberException(type.FullName, name);

        private static FieldInfo RequiredField(Type type, string name, BindingFlags flags)
            => type.GetField(name, flags) ??
               throw new MissingMemberException(type.FullName, name);

        private static MethodInfo RequiredMethod(Type type, string name, Type[] parameters)
            => type.GetMethod(name, InstanceMembers, null, parameters, null) ??
               throw new MissingMemberException(type.FullName, name);

        private sealed class UnityGameViewTarget : IGameViewTarget
        {
            private readonly EditorWindow window;
            private readonly PropertyInfo selectedSizeIndexProperty;
            private readonly FieldInfo renderTextureField;

            public UnityGameViewTarget(
                EditorWindow window,
                PropertyInfo selectedSizeIndexProperty,
                FieldInfo renderTextureField)
            {
                this.window = window;
                this.selectedSizeIndexProperty = selectedSizeIndexProperty;
                this.renderTextureField = renderTextureField;
            }

            public int SelectedResolutionIndex
            {
                get => (int)selectedSizeIndexProperty.GetValue(window, null);
                set => selectedSizeIndexProperty.SetValue(window, value, null);
            }

            public bool Maximized
            {
                get => window.maximized;
                set => window.maximized = value;
            }

            public RenderTexture RenderTexture
                => renderTextureField.GetValue(window) as RenderTexture;

            public bool TryGetRenderTextureSize(out int width, out int height)
            {
                var renderTexture = RenderTexture;
                if (renderTexture == null || !renderTexture.IsCreated())
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                width = renderTexture.width;
                height = renderTexture.height;
                return true;
            }

            public void Repaint() => window.Repaint();
        }
    }
}

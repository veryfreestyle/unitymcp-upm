using System.Collections.Generic;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    public sealed class GameViewResolutionInfo
    {
        public GameViewResolutionInfo(int index, string name, string mode, int width, int height)
        {
            Index = index;
            Name = name ?? string.Empty;
            Mode = mode ?? string.Empty;
            Width = width;
            Height = height;
        }

        public int Index { get; }
        public string Name { get; }
        public string Mode { get; }
        public int Width { get; }
        public int Height { get; }
        public bool HasDimensions => Width > 0 && Height > 0;
    }

    public interface IGameViewTarget
    {
        int SelectedResolutionIndex { get; set; }
        bool Maximized { get; set; }
        RenderTexture RenderTexture { get; }
        bool TryGetRenderTextureSize(out int width, out int height);
        void Repaint();
    }

    public sealed class GameViewTargetResult
    {
        private GameViewTargetResult(
            bool ok, IGameViewTarget target, string errorCode, string error)
        {
            Ok = ok;
            Target = target;
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Ok { get; }
        public IGameViewTarget Target { get; }
        public string ErrorCode { get; }
        public string Error { get; }

        public static GameViewTargetResult Success(IGameViewTarget target)
            => new GameViewTargetResult(true, target, null, null);

        public static GameViewTargetResult Failure(string errorCode, string error)
            => new GameViewTargetResult(false, null, errorCode, error);
    }

    public interface IGameViewEnvironment
    {
        GameViewTargetResult FindTarget();
        IReadOnlyList<GameViewResolutionInfo> ListResolutions();
    }
}

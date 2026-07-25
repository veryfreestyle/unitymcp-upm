using System;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands.GameView;

namespace VeryFS.UnityMCP.Editor.Commands.Screenshot
{
    public struct CaptureResult
    {
        public bool Ok;
        public Color32[] Pixels;
        public int Width;
        public int Height;
        public string ErrorCode;
        public string Error;
    }

    public interface IGameViewCapturer
    {
        CaptureResult Capture(int maxEdge);
    }

    // Reads the Editor GameView's internal render texture. Requires an open,
    // rendering Game View window; unavailable in -nographics batchmode.
    public sealed class UnityGameViewCapturer : IGameViewCapturer
    {
        private readonly IGameViewEnvironment environment;

        public UnityGameViewCapturer()
            : this(new UnityGameViewEnvironment())
        {
        }

        public UnityGameViewCapturer(IGameViewEnvironment environment)
        {
            this.environment = environment;
        }

        public CaptureResult Capture(int maxEdge)
        {
            var lookup = environment.FindTarget();
            if (!lookup.Ok)
                return new CaptureResult
                {
                    Ok = false,
                    ErrorCode = lookup.ErrorCode,
                    Error = lookup.Error
                };

            lookup.Target.Repaint();
            var sourceRt = lookup.Target.RenderTexture;
            if (sourceRt == null || !sourceRt.IsCreated())
            {
                return new CaptureResult
                {
                    Ok = false,
                    ErrorCode = "game_view_unavailable",
                    Error = "Game View render texture unavailable"
                };
            }

            int srcW = sourceRt.width, srcH = sourceRt.height;
            float scale = Mathf.Min(1f, (float)maxEdge / Mathf.Max(srcW, srcH));
            int w = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            var prev = RenderTexture.active;
            RenderTexture scaled = null;
            Texture2D tex = null;
            try
            {
                var readSource = sourceRt;
                if (scale < 1f)
                {
                    scaled = RenderTexture.GetTemporary(w, h, 0, sourceRt.format);
                    Graphics.Blit(sourceRt, scaled);
                    readSource = scaled;
                }
                RenderTexture.active = readSource;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var pixels = tex.GetPixels32();
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    FlipVertically(pixels, w, h);
                }
                return new CaptureResult { Ok = true, Pixels = pixels, Width = w, Height = h };
            }
            finally
            {
                RenderTexture.active = prev;
                if (scaled != null) RenderTexture.ReleaseTemporary(scaled);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void FlipVertically(Color32[] pixels, int w, int h)
        {
            var row = new Color32[w];
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w, bottom = (h - 1 - y) * w;
                Array.Copy(pixels, top, row, 0, w);
                Array.Copy(pixels, bottom, pixels, top, w);
                Array.Copy(row, 0, pixels, bottom, w);
            }
        }
    }
}

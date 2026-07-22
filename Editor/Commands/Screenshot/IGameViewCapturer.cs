using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.Screenshot
{
    public struct CaptureResult
    {
        public bool Ok;
        public Color32[] Pixels;
        public int Width;
        public int Height;
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
        public CaptureResult Capture(int maxEdge)
        {
            var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                return new CaptureResult { Ok = false, Error = "GameView type not found" };
            }
            var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
            if (gameView == null)
            {
                return new CaptureResult { Ok = false, Error = "No Game View window is open" };
            }
            gameView.Repaint();
            var rtField = gameViewType.GetField("m_RenderTexture", BindingFlags.NonPublic | BindingFlags.Instance);
            var sourceRt = rtField?.GetValue(gameView) as RenderTexture;
            if (sourceRt == null || !sourceRt.IsCreated())
            {
                return new CaptureResult { Ok = false, Error = "Game View render texture unavailable" };
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

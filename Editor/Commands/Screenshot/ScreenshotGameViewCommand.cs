using System.IO;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Infrastructure;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Screenshot
{
    public sealed class ScreenshotGameViewCommand : IRpcCommand
    {
        private const int MinEdge = 64;
        private const int MaxEdge = 4096;
        private const int DefaultEdge = 1568;

        private readonly IGameViewCapturer capturer;
        private readonly string screenshotDir;
        private readonly IIdGenerator ids;

        public ScreenshotGameViewCommand(IGameViewCapturer capturer, string screenshotDir, IIdGenerator ids)
        {
            this.capturer = capturer;
            this.screenshotDir = screenshotDir;
            this.ids = ids;
        }

        public string Method => RpcMethods.ScreenshotGameView;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "screenshot-game-view",
            RpcMethod = RpcMethods.ScreenshotGameView,
            Title = "Screenshot / Game View",
            Description = "Capture the Editor Game View and return it as an image for visual inspection. Requires an open Game View.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("maxEdge", JsonRpcSerializer.Object(("type", "integer"), ("minimum", MinEdge), ("maximum", MaxEdge))),
                    ("format", JsonRpcSerializer.Object(("type", "string"), ("enum", Enum("png", "jpeg")))),
                    ("quality", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 1), ("maximum", 100)))))),
            Annotations = JsonRpcSerializer.Object(("readOnlyHint", true), ("idempotentHint", true))
        };

        public static int ClampMaxEdge(int requested)
        {
            if (requested < MinEdge) return MinEdge;
            if (requested > MaxEdge) return MaxEdge;
            return requested;
        }

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            int maxEdge = ClampMaxEdge(ReadInt(request.Params, "maxEdge", DefaultEdge));
            string format = ReadString(request.Params, "format") ?? "png";
            int quality = ReadInt(request.Params, "quality", 85);

            var capture = capturer.Capture(maxEdge);
            if (!capture.Ok)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidEditorState,
                    capture.Error ?? "Game View is not available.",
                    JsonRpcSerializer.Object(("errorCode", "invalid_editor_state"))));
            }

            var tex = new Texture2D(capture.Width, capture.Height, TextureFormat.RGB24, false);
            byte[] bytes;
            string mimeType, ext;
            try
            {
                tex.SetPixels32(capture.Pixels);
                tex.Apply();
                if (format == "jpeg")
                {
                    bytes = tex.EncodeToJPG(quality);
                    mimeType = "image/jpeg";
                    ext = "jpg";
                }
                else
                {
                    bytes = tex.EncodeToPNG();
                    mimeType = "image/png";
                    ext = "png";
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }

            Directory.CreateDirectory(screenshotDir);
            var path = Path.Combine(screenshotDir, ids.NewId("shot") + "." + ext);
            File.WriteAllBytes(path, bytes);

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("imageFile", JsonRpcSerializer.Object(
                    ("path", path),
                    ("width", capture.Width),
                    ("height", capture.Height),
                    ("mimeType", mimeType),
                    ("byteCount", bytes.Length)))));
        }

        private static JsonData Enum(params string[] values)
        {
            var data = new JsonData();
            data.SetJsonType(JsonType.Array);
            foreach (var v in values) data.Add(v);
            return data;
        }

        private static int ReadInt(JsonData p, string key, int fallback) =>
            p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int)p[key] : fallback;

        private static string ReadString(JsonData p, string key) =>
            p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}

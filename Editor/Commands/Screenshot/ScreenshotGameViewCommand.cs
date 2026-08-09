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
        private const int DefaultEdge = 1920;

        private readonly IGameViewCapturer capturer;
        private readonly string screenshotDir;
        private readonly IIdGenerator ids;
        private readonly string projectRoot;

        public ScreenshotGameViewCommand(
            IGameViewCapturer capturer, string screenshotDir, IIdGenerator ids, string projectRoot)
        {
            this.capturer = capturer;
            this.screenshotDir = screenshotDir;
            this.ids = ids;
            this.projectRoot = projectRoot;
        }

        public string Method => RpcMethods.ScreenshotGameView;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "screenshot-game-view",
            RpcMethod = RpcMethods.ScreenshotGameView,
            Title = "Screenshot / Game View",
            Description = "Capture the Editor Game View for visual inspection. Requires an open Game View. " +
                "The file path and metadata always come back as text; inlineImage controls the base64 image block: " +
                "omit it for the project default (set in the Server Monitor window), true to see the picture in this turn, " +
                "false to save context and read the file at the returned path instead. " +
                "The inline image stays in the conversation history for the rest of the session. " +
                "If you cannot read image content returned by MCP tools, always pass false and open the file at the returned path instead.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("maxEdge", JsonRpcSerializer.Object(("type", "integer"), ("minimum", MinEdge), ("maximum", MaxEdge))),
                    ("format", JsonRpcSerializer.Object(("type", "string"), ("enum", Enum("png", "jpeg")))),
                    ("quality", JsonRpcSerializer.Object(("type", "integer"), ("minimum", 1), ("maximum", 100))),
                    // No schema "default" on purpose: the real default lives in EditorPrefs and can
                    // change at any time, so pinning one here would create a second source of truth.
                    ("inlineImage", JsonRpcSerializer.Object(("type", "boolean")))))),
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
            bool inlineImage = ReadBoolNullable(request.Params, "inlineImage")
                ?? ScreenshotPreferences.LoadInlineImageDefault(projectRoot);

            var capture = capturer.Capture(maxEdge);
            if (!capture.Ok)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidEditorState,
                    capture.Error ?? "Game View is not available.",
                    JsonRpcSerializer.Object((
                        "errorCode", capture.ErrorCode ?? "invalid_editor_state"))));
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
                    ("byteCount", bytes.Length))),
                ("inlineImage", inlineImage)));
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

        // Nullable so an explicit false is distinguishable from an absent field:
        // absent falls back to EditorPrefs, false does not.
        private static bool? ReadBoolNullable(JsonData p, string key) =>
            p != null && p.IsObject && p.ContainsKey(key) && p[key].IsBoolean ? (bool?)(bool)p[key] : null;

        private static string ReadString(JsonData p, string key) =>
            p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}

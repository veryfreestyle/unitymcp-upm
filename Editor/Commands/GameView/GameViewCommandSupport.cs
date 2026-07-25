using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.GameView
{
    internal static class GameViewCommandSupport
    {
        public static JsonRpcResponse AsyncOnly(string requestId)
            => JsonRpcResponse.FromError(requestId, new JsonRpcError(
                JsonRpcErrorCodes.InternalError,
                "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public static JsonRpcResponse TargetError(string requestId, GameViewTargetResult target)
            => JsonRpcResponse.FromError(requestId, new JsonRpcError(
                JsonRpcErrorCodes.InvalidEditorState,
                target.Error ?? "Game View is not available.",
                JsonRpcSerializer.Object(
                    ("errorCode", target.ErrorCode ?? "game_view_unavailable"))));

        public static JsonRpcResponse InvalidParams(
            string requestId, string message, string errorCode = "invalid_params")
            => JsonRpcResponse.FromError(requestId, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams,
                message,
                JsonRpcSerializer.Object(("errorCode", errorCode))));

        public static JsonData BuildState(
            IGameViewTarget target,
            IReadOnlyList<GameViewResolutionInfo> resolutions,
            bool? applied = null,
            bool? settled = null)
        {
            var result = JsonRpcSerializer.Object(("available", true));
            int selectedIndex = target.SelectedResolutionIndex;
            result["selectedIndex"] = selectedIndex;

            if (selectedIndex >= 0 && selectedIndex < resolutions.Count)
            {
                result["selectedResolution"] = ResolutionJson(resolutions[selectedIndex]);
            }

            result["maximized"] = target.Maximized;
            result["actualRenderTexture"] = RenderTextureJson(target);

            if (applied.HasValue) result["applied"] = applied.Value;
            if (settled.HasValue) result["renderTextureSettled"] = settled.Value;
            return result;
        }

        public static JsonData ResolutionJson(GameViewResolutionInfo resolution)
        {
            var result = JsonRpcSerializer.Object(
                ("index", resolution.Index),
                ("name", resolution.Name),
                ("mode", resolution.Mode));
            if (resolution.HasDimensions)
            {
                result["width"] = resolution.Width;
                result["height"] = resolution.Height;
            }
            return result;
        }

        private static JsonData RenderTextureJson(IGameViewTarget target)
        {
            if (!target.TryGetRenderTextureSize(out int width, out int height))
            {
                return JsonRpcSerializer.Object(("available", false));
            }
            return JsonRpcSerializer.Object(
                ("available", true),
                ("width", width),
                ("height", height));
        }

        public static RpcToolDescriptor Descriptor(
            string name,
            string method,
            string title,
            string description,
            JsonData properties,
            bool readOnly)
            => new RpcToolDescriptor
            {
                Name = name,
                RpcMethod = method,
                Title = title,
                Description = description,
                Completion = "response",
                FailureMode = "error",
                InputSchema = JsonRpcSerializer.Object(
                    ("type", "object"),
                    ("additionalProperties", false),
                    ("properties", properties)),
                Annotations = JsonRpcSerializer.Object(
                    ("readOnlyHint", readOnly),
                    ("idempotentHint", true))
            };
    }
}

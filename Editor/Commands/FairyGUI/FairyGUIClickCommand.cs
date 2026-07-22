using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIClickCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private const int MaxFrames = 240;
        private readonly IPanelSource source;
        private readonly IStageInput stageInput;
        private readonly IFrameStepper stepper;

        public FairyGUIClickCommand(IPanelSource source, IStageInput stageInput, IFrameStepper stepper)
        {
            this.source = source;
            this.stageInput = stageInput;
            this.stepper = stepper;
        }

        public string Method => RpcMethods.FairyGuiClick;
        public string Group => "fgui.input";
        public string Action => "click";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-click",
            RpcMethod = RpcMethods.FairyGuiClick,
            Title = "FairyGUI / Click",
            Description = "Click a FairyGUI object as a real user would. mode:\"real\" (default) drives the input " +
                "pipeline at the target's coordinates (any control, fires onClick, opens combobox dropdowns, hits list items); " +
                "requires the target visible and on-screen. mode:\"direct\" is a coordinate-free synchronous GButton click " +
                "(bypasses the pipeline; use when the button is occluded/off-screen or a single-frame result is needed; " +
                "non-GButton returns unsupported). Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("mode", JsonRpcSerializer.Object(("type", "string")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            int? panelInstanceId = ReadIntNullable(request.Params, "panelInstanceId");
            string path = ReadString(request.Params, "path");
            string mode = ReadString(request.Params, "mode") ?? "real";

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", located.State)));
            var obj = located.Node.Unwrap();
            if (obj == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));

            if (mode == "direct")
            {
                var btn = obj as GButton;
                if (btn == null)
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "unsupported"), ("type", obj.GetType().Name)));
                btn.FireClick(false, true); // downEffect:false 保同步; clickCall:true 跑 onClick
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                    ("state", "ok"), ("mode", "direct"),
                    ("target", JsonRpcSerializer.Object(
                        ("name", btn.name ?? string.Empty), ("type", btn.GetType().Name),
                        ("selected", btn.selected), ("title", btn.title ?? string.Empty)))));
            }

            var center = FairyGUIGesturePlayer.CenterOf(obj);
            var screen = FairyGUIGesturePlayer.StageToScreen(center, stageInput.StageSize);
            var player = new FairyGUIGesturePlayer(stageInput, stepper, MaxFrames);
            bool ok = await player.PlayClick(screen);
            if (!ok)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "timeout")));
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("mode", "real"),
                ("target", JsonRpcSerializer.Object(
                    ("name", obj.name ?? string.Empty), ("type", obj.GetType().Name)))));
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}

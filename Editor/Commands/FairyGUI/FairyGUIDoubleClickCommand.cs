using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIDoubleClickCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private const int MaxFrames = 240;
        private readonly IPanelSource source;
        private readonly IStageInput stageInput;
        private readonly IFrameStepper stepper;

        public FairyGUIDoubleClickCommand(IPanelSource source, IStageInput stageInput, IFrameStepper stepper)
        {
            this.source = source;
            this.stageInput = stageInput;
            this.stepper = stepper;
        }

        public string Method => RpcMethods.FairyGuiDoubleClick;
        public string Group => "fgui.input";
        public string Action => "double-click";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-double-click",
            RpcMethod = RpcMethods.FairyGuiDoubleClick,
            Title = "FairyGUI / Double Click",
            Description = "Double-click a FairyGUI object at its coordinates via the input pipeline (two press/release " +
                "cycles within the double-click window). Fires isDoubleClick. Requires target visible/on-screen. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"), ("description", FairyGUINodeLocator.PanelInstanceIdHelp))),
                    ("path", JsonRpcSerializer.Object(("type", "string"), ("description", FairyGUINodeLocator.PathSyntaxHelp)))))),
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

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, FairyGUINodeLocator.FailurePayload(located));
            var obj = located.Node.Unwrap();
            if (obj == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));

            var screen = FairyGUIGesturePlayer.StageToScreen(
                FairyGUIGesturePlayer.CenterOf(obj), stageInput.StageSize);
            var player = new FairyGUIGesturePlayer(stageInput, stepper, MaxFrames);
            bool ok = await player.PlayDoubleClick(screen);
            if (!ok)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "timeout")));
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"),
                ("target", JsonRpcSerializer.Object(
                    ("name", obj.name ?? string.Empty), ("type", obj.GetType().Name)))));
        }

        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}

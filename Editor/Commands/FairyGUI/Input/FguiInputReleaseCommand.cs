using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputReleaseCommand : FguiInputCommandBase
    {
        public FguiInputReleaseCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputRelease;
        public override string Action => "release";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-release",
            RpcMethod = RpcMethods.FairyGuiInputRelease,
            Title = "FairyGUI / Input / Release",
            Description = "Release a held button where the pointer currently is; to release elsewhere, move "
                + "first. Takes no location. Requires an open session and a matching earlier press.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(AddButtonSchema),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含 Parse 和 PointerSpeedBase 读取) 都在 Guarded 里, 见 FguiInputMoveCommand
        // 顶上的注释: 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                FguiInputRequest req = FguiInputRequest.Parse(request.Params, PointerSpeedBase);
                if (req.Error != null) { return InvalidParams(request, req.ErrorDetail); }
                if (req.HasLocation)
                {
                    return InvalidParams(request, "'release' takes no location: it releases where the "
                        + "pointer currently is. Send a 'move' first to release somewhere else.");
                }

                // 同 FguiInputPressCommand 顶上的注释: IsPlaying 必须排在 HasSession 之前,
                // 否则冷启动(没开过 session, 不在 Play 模式)永远先撞 session_required,
                // 真正的阻塞原因要等下一轮 begin-session 才浮出来。
                if (!Input.IsPlaying)
                {
                    return JsonRpcResponse.FromSuccess(request.Id,
                        JsonRpcSerializer.Object(("state", "not_playing")));
                }
                if (!Sessions.HasSession) { return SessionRequired(request, Action); }

                // 不匹配或压根没按下过返回 error 而非静默 no-op —— ScriptedInputSource.IsMouseHeld
                // 是 internal, MCP 读不到, "button 是否匹配之前的 press" 只能由这里的按下集合判。
                if (!Sessions.IsButtonPressed(req.Button))
                {
                    return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                        JsonRpcErrorCodes.InvalidParams,
                        "button " + req.Button + " is not held; press it first.",
                        JsonRpcSerializer.Object(("errorCode", "not_pressed"))));
                }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    Vector2 point = Input.CurrentPointerPosition;
                    McpRunOutcome outcome = await Input.RunAsync(lease.Player.Release(point, req.Button));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    Sessions.NoteReleased(req.Button);
                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.TouchTarget, req, null));
                }
            });
        }
    }
}

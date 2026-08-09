using System;
using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FguiInputBeginSessionCommand : FguiInputCommandBase
    {
        public FguiInputBeginSessionCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputBeginSession;
        public override string Action => "begin-session";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-begin-session",
            RpcMethod = RpcMethods.FairyGuiInputBeginSession,
            Title = "FairyGUI / Input / Begin session",
            Description = "Keep pointer and keyboard state alive across several calls. Needed by press, "
                + "release and step, and by anything that depends on rollover state such as tooltips. "
                + "Close it with end-session.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                p["label"] = JsonRpcSerializer.Object(
                    ("type", "string"), ("description", "What this session is doing; shown when a later call conflicts."));
                p["force"] = JsonRpcSerializer.Object(
                    ("type", "boolean"), ("description", "Take over an existing session instead of reporting a conflict. Default false."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // Sessions.BeginExplicit -> StartSession -> input.Start 是会抛的真实路径
        // (McpStageInputSessionManager 自己称之为"真实路径"), 所以这里也整段进 Guarded,
        // 不只是 press/release/step 那三个才需要。
        // body 标 async: force 抢占时 Sessions.BeginExplicit 要先跑一次 ReleaseHeld 才关掉
        // 上一个 session(见 McpStageInputSessionManager.BeginExplicit 的注释), 那一步要推帧。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                string error;
                string label = FguiInputRequest.ReadString(request.Params, "label", out error);
                if (error != null) { return InvalidParams(request, error); }
                if (string.IsNullOrEmpty(label))
                {
                    return InvalidParams(request,
                        "'label' is required and must be a non-empty string.");
                }
                bool force = FguiInputRequest.ReadBool(request.Params, "force", false, out error);
                if (error != null) { return InvalidParams(request, error); }

                // conflict 要在 BeginExplicit 改掉状态之前把现有 label / age 读出来。
                string existingLabel = Sessions.SessionLabel;
                double existingAge = Sessions.SessionAgeSeconds;

                string outcome = await Sessions.BeginExplicit(label, force);
                if (outcome == "conflict")
                {
                    JsonData conflict = JsonRpcSerializer.Object(("state", "conflict"));
                    conflict["session"] = JsonRpcSerializer.Object(
                        ("label", existingLabel ?? string.Empty),
                        ("ageSeconds", Math.Round(existingAge, 1)));
                    return JsonRpcResponse.FromSuccess(request.Id, conflict);
                }
                if (outcome != null)
                {
                    return JsonRpcResponse.FromSuccess(request.Id,
                        JsonRpcSerializer.Object(("state", outcome)));
                }

                return JsonRpcResponse.FromSuccess(request.Id, Payload("ok", null, null, null));
            });
        }
    }

    public sealed class FguiInputEndSessionCommand : FguiInputCommandBase
    {
        public FguiInputEndSessionCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputEndSession;
        public override string Action => "end-session";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-end-session",
            RpcMethod = RpcMethods.FairyGuiInputEndSession,
            Title = "FairyGUI / Input / End session",
            Description = "Close the open session and hand pointer and keyboard back. Idempotent: "
                + "returns state no_session when nothing was open.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p => { }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        // Sessions.EndExplicit -> CloseSession -> input.Dispose 抛出也是真实路径
        // (同一份注释里点名的另一半), 同样整段进 Guarded。EndExplicit 本身在真正关闭前
        // 会先跑 ReleaseHeld(还有按钮按着的话), 这一步要推帧, 所以是 await 而不是
        // UniTask.FromResult 包一层同步值(review Important 4)。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                bool closed = await Sessions.EndExplicit();
                return JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", closed ? "ok" : "no_session")));
            });
        }
    }
}

using Cysharp.Threading.Tasks;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 逐字符打字到当前焦点控件, 插入到光标处而非替换整段文本。自足动作, 不需要预先存在
    /// 的 session, 原因与 send-key 相同。target 报的是发键时的 FocusTarget。
    /// </summary>
    public sealed class FguiInputTypeTextCommand : FguiInputCommandBase
    {
        public FguiInputTypeTextCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputTypeText;
        public override string Action => "type-text";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-type-text",
            RpcMethod = RpcMethods.FairyGuiInputTypeText,
            Title = "FairyGUI / Input / Type text",
            Description = "Type text into the focused control, one character per step, inserting at the "
                + "caret rather than replacing. Prefer this over setting a field's text directly: writing "
                + "the text skips onChanged, maxLength truncation, IME and any live validation, so it proves "
                + "nothing about whether a real user could type it. Needs no session.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                p["text"] = JsonRpcSerializer.Object(("type", "string"), ("description", "Text to type."));
                p["framesPerChar"] = JsonRpcSerializer.Object(
                    ("type", "integer"),
                    ("description", "Frames between characters. Default 1. Mutually exclusive with msPerChar."));
                p["msPerChar"] = JsonRpcSerializer.Object(
                    ("type", "number"),
                    ("description", "Milliseconds between characters, for human-paced typing. "
                        + "Mutually exclusive with framesPerChar."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        // 整段 body (含参数解析) 都在 Guarded 里, 见 FguiInputWheelCommand 顶上的注释:
        // 这批 action 里没有代码站在 Guarded 外面。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, async () =>
            {
                string error;
                string text = FguiInputRequest.ReadString(request.Params, "text", out error);
                if (error != null) { return InvalidParams(request, error); }
                if (text == null) { return InvalidParams(request, "'text' is required."); }

                int? framesPerChar = FguiInputRequest.ReadIntNullable(request.Params, "framesPerChar", out error);
                if (error != null) { return InvalidParams(request, error); }
                float? msPerChar = FguiInputRequest.ReadFloatNullable(request.Params, "msPerChar", out error);
                if (error != null) { return InvalidParams(request, error); }

                if (framesPerChar.HasValue && msPerChar.HasValue)
                {
                    return InvalidParams(request, "framesPerChar and msPerChar are mutually exclusive: "
                        + "the first is frame-driven, the second wall-clock driven.");
                }
                if (framesPerChar.HasValue && framesPerChar.Value < 1)
                {
                    return InvalidParams(request, "framesPerChar must be at least 1.");
                }
                if (msPerChar.HasValue && msPerChar.Value < 0f)
                {
                    return InvalidParams(request, "msPerChar cannot be negative.");
                }

                // 预算校验必须在 Acquire (会碰 Stage 输入状态) 之前完成。逐字符成本是这个
                // action 独有的规模上限来源: text 越长, 帧/墙钟花费线性增长。
                var budget = new FguiInputBudget();
                if (msPerChar.HasValue)
                {
                    budget.AddMs(msPerChar.Value * text.Length);

                    // 不是拿 ms 折算帧(两类独立校验、不互相折算): 这记的是 fork 真实要花的
                    // 帧数。TypeTextAtRateRoutine 对每个字符都调 WaitMsRoutine(msPerChar, 1),
                    // minFrames = 1 是无条件的 —— 哪怕 msPerChar 是 0, 每个字符仍至少推进
                    // 一帧。不记这笔账, msPerChar=0 配一个足够长的 text 会让 ms 桶按公式算出 0、
                    // frames 桶完全没碰到, 两个桶都不违规地跑掉几十万甚至上百万帧。
                    budget.AddFrames(text.Length);
                }
                else
                {
                    // (framesPerChar ?? 1) * text.Length 是两个数各自只校验了下限的乘积:
                    // framesPerChar 没有上限校验, text.Length 只受 8 MiB 传输层限制, 50000 ×
                    // 50000 = 25 亿就超过 int32 的 21 亿上限, 折回负数后会被 AddFrames 的
                    // "value > 0" 门悄悄吞掉、budget 记成 0。这里先用 long 算出真实乘积,
                    // 不会重蹈同样的溢出。
                    long frameCost = (long)(framesPerChar ?? 1) * text.Length;
                    budget.AddFrames(frameCost);
                }
                string violation = budget.Violation();
                if (violation != null) { return InvalidParams(request, violation); }

                using (McpInputLease lease = Sessions.Acquire(Method))
                {
                    if (lease.Error != null)
                    {
                        return JsonRpcResponse.FromSuccess(request.Id,
                            JsonRpcSerializer.Object(("state", lease.Error)));
                    }

                    // 代理对与 emoji 不做检测也不拒绝: 逐 char 投递会把代理对自动拆成两个 char,
                    // InputTextField.HandleTextInput 按 IsHighSurrogate / IsLowSurrogate 分两次收。
                    // 能否显示取决于字体是否含该字形, 输入本身一定成立。
                    McpRunOutcome outcome = await Input.RunAsync(msPerChar.HasValue
                        ? lease.Player.TypeTextAtRate(text, msPerChar.Value)
                        : lease.Player.TypeText(text, framesPerChar ?? 1));
                    if (!outcome.Completed) { return Fault(request, outcome); }

                    return JsonRpcResponse.FromSuccess(request.Id,
                        Payload("ok", Input.FocusTarget, null, null));
                }
            });
        }
    }
}

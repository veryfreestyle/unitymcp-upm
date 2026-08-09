using System;
using Cysharp.Threading.Tasks;
using LitJson;
using VeryFS.UnityMCP.Editor.Commands.FairyGUI;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands
{
    // Runs a sequence of sub-commands (plus a `wait` pseudo-command) serially
    // in one call. Sync sub-commands dispatch via Handle, async via HandleAsync.
    // Long-running commands, nested batch, and unknown methods are rejected
    // per-entry. Results are aggregated; failFast (default false) stops early.
    public sealed class BatchExecuteCommand : IAsyncRpcCommand
    {
        private const string WaitTool = "wait";

        private readonly RpcCommandRegistry registry;
        private readonly IFrameStepper frameStepper;
        private readonly IDelayProvider delayProvider;
        private readonly IMcpImplicitSessionHost inputSessions;

        public BatchExecuteCommand(
            RpcCommandRegistry registry,
            IFrameStepper frameStepper,
            IDelayProvider delayProvider,
            IMcpImplicitSessionHost inputSessions)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.frameStepper = frameStepper ?? throw new ArgumentNullException(nameof(frameStepper));
            this.delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
            this.inputSessions = inputSessions ?? throw new ArgumentNullException(nameof(inputSessions));
        }

        public string Method => RpcMethods.BatchExecute;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "batch-execute",
            RpcMethod = RpcMethods.BatchExecute,
            Title = "Batch / Execute",
            Description =
                "Run a sequence of RPC sub-commands serially in one call and return their aggregated results. " +
                "Each entry is {\"tool\": <rpcMethod>, \"params\": {...}}." +
                " For grouped tools the rpcMethod is the group, with the action in params: " +
                "{\"tool\":\"fgui.input\",\"params\":{\"action\":\"click\",\"path\":\"MainPanel/btn\"}}." +
                " Sub-commands run in order; results align " +
                "by index. failFast (default false): stop after the first failing sub-command (a sub-command fails " +
                "when it returns an error). Pseudo-command \"wait\" pauses between steps: " +
                "{\"tool\":\"wait\",\"params\":{\"ms\":500}} or {\"tool\":\"wait\",\"params\":{\"frames\":3}} " +
                "(ms and frames are mutually exclusive; no upper bound — bound the whole batch via timeoutMs). " +
                "NOT supported as sub-commands: assets.refresh, editor.application.set-state, test.run " +
                "(long-running), and batch.execute (no nesting)." +
                " The batch call itself succeeds even when sub-commands fail: always check each entry in " +
                "results, not just the top-level response.",
            Completion = "response",
            FailureMode = "error",
            DefaultTimeoutMs = 300000,
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"),
                ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("commands", JsonRpcSerializer.Object(
                        ("type", "array"),
                        ("items", JsonRpcSerializer.Object(
                            ("type", "object"),
                            ("properties", JsonRpcSerializer.Object(
                                ("tool", JsonRpcSerializer.Object(("type", "string"))),
                                ("params", JsonRpcSerializer.Object(("type", "object"))))),
                            ("required", ToArray("tool")))))),
                    ("failFast", JsonRpcSerializer.Object(("type", "boolean"))))),
                ("required", ToArray("commands"))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            var commands = request.Params != null && request.Params.IsObject &&
                request.Params.ContainsKey("commands")
                ? request.Params["commands"]
                : null;

            if (commands == null || !commands.IsArray)
            {
                return JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                    JsonRpcErrorCodes.InvalidParams, "'commands' must be an array.",
                    JsonRpcSerializer.Object(("errorCode", "invalid_params"))));
            }

            bool failFast = request.Params.ContainsKey("failFast") &&
                request.Params["failFast"].IsBoolean && (bool)request.Params["failFast"];

            var results = new JsonData();
            results.SetJsonType(JsonType.Array);
            int successCount = 0;
            int failureCount = 0;

            // 批内含指针类调用时, 一批共用一次接管: 逐条各开各关会在命令之间清掉
            // TouchInfo, 拖拽中途插一张截图就断了。batch 禁嵌套、禁长耗时命令,
            // 所以边界天然不重叠; 批开始时已有显式 session 则沿用, 不另开也不在批尾关。
            //
            // 不能再写成 using(...): 批尾如果真的要关闭底层 session 且还有按钮按着,
            // 必须先 await 一次 ReleaseHeld(review Important 4) —— using 生成的 Dispose
            // 是同步调用点, 塞不进一次跨帧的 await。改成显式 try/finally, finally 里
            // await EndImplicitSessionAsync, 语义跟 using 一致(正常退出/异常穿出都会执行),
            // 只是把"关闭"这一步换成了能在关闭前先补一次异步收尾的版本。
            IDisposable implicitScope = inputSessions.BeginImplicitSession();
            try
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    JsonData entryResult = await ExecuteEntry(commands[i], i);
                    results.Add(entryResult);
                    if ((bool)entryResult["ok"]) { successCount++; } else { failureCount++; }

                    if (failFast && !(bool)entryResult["ok"]) { break; }
                }
            }
            finally
            {
                await inputSessions.EndImplicitSessionAsync(implicitScope);
            }

            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("results", results),
                ("successCount", successCount),
                ("failureCount", failureCount)));
        }

        private async UniTask<JsonData> ExecuteEntry(JsonData entry, int index)
        {
            if (entry == null || !entry.IsObject || !entry.ContainsKey("tool") || !entry["tool"].IsString)
            {
                return Failure(string.Empty, JsonRpcErrorCodes.InvalidParams,
                    "Command entry " + index + " must be an object with a string 'tool'.");
            }

            string tool = (string)entry["tool"];
            JsonData subParams = entry.ContainsKey("params") ? entry["params"] : null;

            if (tool == WaitTool)
            {
                return await ExecuteWait(subParams);
            }

            if (!registry.TryGet(tool, out var command))
            {
                return Failure(tool, JsonRpcErrorCodes.MethodNotFound, "Unknown RPC method: " + tool + ".");
            }

            if (tool == RpcMethods.BatchExecute)
            {
                return Failure(tool, JsonRpcErrorCodes.InvalidParams,
                    "batch.execute does not support nesting.");
            }

            if (command is ILongRunningCommand)
            {
                return Failure(tool, JsonRpcErrorCodes.InvalidParams,
                    "Command '" + tool + "' is a long-running command and is not allowed in a batch.");
            }

            var subRequest = JsonRpcRequest.Create("batch-" + index, tool, subParams);

            if (command is IAsyncRpcCommand asyncCommand)
            {
                try
                {
                    var response = await asyncCommand.HandleAsync(subRequest);
                    return FromResponse(tool, response);
                }
                catch (Exception ex)
                {
                    return Failure(tool, JsonRpcErrorCodes.InternalError, ex.Message);
                }
            }

            try
            {
                var syncResponse = command.Handle(subRequest);
                return FromResponse(tool, syncResponse);
            }
            catch (Exception ex)
            {
                return Failure(tool, JsonRpcErrorCodes.InternalError, ex.Message);
            }
        }

        private async UniTask<JsonData> ExecuteWait(JsonData waitParams)
        {
            bool hasMs = waitParams != null && waitParams.IsObject &&
                waitParams.ContainsKey("ms") && waitParams["ms"].IsInt;
            bool hasFrames = waitParams != null && waitParams.IsObject &&
                waitParams.ContainsKey("frames") && waitParams["frames"].IsInt;

            if (hasMs == hasFrames)
            {
                return Failure(WaitTool, JsonRpcErrorCodes.InvalidParams,
                    "wait requires exactly one of integer 'ms' or 'frames'.");
            }

            if (hasMs)
            {
                int ms = (int)waitParams["ms"];
                if (ms < 0)
                {
                    return Failure(WaitTool, JsonRpcErrorCodes.InvalidParams, "wait 'ms' must be >= 0.");
                }
                await delayProvider.Delay(ms);
                return WaitResult(JsonRpcSerializer.Object(("ms", ms)));
            }

            int frames = (int)waitParams["frames"];
            if (frames < 0)
            {
                return Failure(WaitTool, JsonRpcErrorCodes.InvalidParams, "wait 'frames' must be >= 0.");
            }
            for (int f = 0; f < frames; f++)
            {
                await frameStepper.NextFrame();
            }
            return WaitResult(JsonRpcSerializer.Object(("frames", frames)));
        }

        private static JsonData WaitResult(JsonData waited)
        {
            return JsonRpcSerializer.Object(
                ("tool", WaitTool),
                ("ok", true),
                ("result", JsonRpcSerializer.Object(("waited", waited))));
        }

        private static JsonData FromResponse(string tool, JsonRpcResponse response)
        {
            if (response.Error != null)
            {
                return Failure(tool, response.Error.Code, response.Error.Message);
            }

            return JsonRpcSerializer.Object(
                ("tool", tool),
                ("ok", true),
                ("result", response.Result));
        }

        private static JsonData Failure(string tool, int code, string message)
        {
            return JsonRpcSerializer.Object(
                ("tool", tool),
                ("ok", false),
                ("error", JsonRpcSerializer.Object(("code", code), ("message", message))));
        }

        private static JsonData ToArray(params string[] values)
        {
            var array = new JsonData();
            array.SetJsonType(JsonType.Array);
            foreach (var value in values) { array.Add(value); }
            return array;
        }

    }
}

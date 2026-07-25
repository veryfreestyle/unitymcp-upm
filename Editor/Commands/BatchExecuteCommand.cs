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

        public BatchExecuteCommand(
            RpcCommandRegistry registry,
            IFrameStepper frameStepper,
            IDelayProvider delayProvider)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.frameStepper = frameStepper ?? throw new ArgumentNullException(nameof(frameStepper));
            this.delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        }

        public string Method => RpcMethods.BatchExecute;

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "batch-execute",
            RpcMethod = RpcMethods.BatchExecute,
            Title = "Batch / Execute",
            Description =
                "Run a sequence of RPC sub-commands serially in one call and return their aggregated results. " +
                "Each entry is {\"tool\": <rpcMethod>, \"params\": {...}}. Sub-commands run in order; results align " +
                "by index. failFast (default false): stop after the first failing sub-command (a sub-command fails " +
                "when it returns an error). Pseudo-command \"wait\" pauses between steps: " +
                "{\"tool\":\"wait\",\"params\":{\"ms\":500}} or {\"tool\":\"wait\",\"params\":{\"frames\":3}} " +
                "(ms and frames are mutually exclusive; no upper bound — bound the whole batch via timeoutMs). " +
                "NOT supported as sub-commands: assets.refresh, editor.application.set-state, test.run " +
                "(long-running), and batch.execute (no nesting).",
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

            for (int i = 0; i < commands.Count; i++)
            {
                JsonData entryResult = await ExecuteEntry(commands[i], i);
                results.Add(entryResult);
                if ((bool)entryResult["ok"]) { successCount++; } else { failureCount++; }

                if (failFast && !(bool)entryResult["ok"]) { break; }
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

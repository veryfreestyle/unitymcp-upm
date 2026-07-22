using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FairyGUI;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public sealed class FairyGUIGestureCommand : IGroupedCommand, IAsyncRpcCommand
    {
        private const int MaxFrames = 240;
        private const int DefaultSteps = 8;
        private const int DefaultCircleSegments = 24;
        private readonly IPanelSource source;
        private readonly IStageInput stageInput;
        private readonly IFrameStepper stepper;

        public FairyGUIGestureCommand(IPanelSource source, IStageInput stageInput, IFrameStepper stepper)
        {
            this.source = source; this.stageInput = stageInput; this.stepper = stepper;
        }

        public string Method => RpcMethods.FairyGuiGesture;
        public string Group => "fgui.input";
        public string Action => "gesture";

        public RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-gesture",
            RpcMethod = RpcMethods.FairyGuiGesture,
            Title = "FairyGUI / Gesture",
            Description = "Single-finger continuous gesture via the input pipeline: straight drag (to.dx/dy or to.toPath), " +
                "arbitrary path (pathPoints, offsets from the start control center), circle (pathShape), or long-press (holdFrames). " +
                "Fires onDragStart/Move/End, onGripTouchEnd, scroll onScrollEnd/pull-refresh, SwipeGesture, LongPressGesture. " +
                "Multi-finger (pinch/rotate) not supported. Requires target visible/on-screen. Play mode only.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false),
                ("properties", JsonRpcSerializer.Object(
                    ("panelInstanceId", JsonRpcSerializer.Object(("type", "integer"))),
                    ("path", JsonRpcSerializer.Object(("type", "string"))),
                    ("to", JsonRpcSerializer.Object(("type", "object"))),
                    ("pathPoints", JsonRpcSerializer.Object(("type", "array"))),
                    ("pathShape", JsonRpcSerializer.Object(("type", "object"))),
                    ("steps", JsonRpcSerializer.Object(("type", "integer"))),
                    ("holdFrames", JsonRpcSerializer.Object(("type", "integer")))))),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", false))
        };

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public async UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            var p = request.Params;
            int? panelInstanceId = ReadIntNullable(p, "panelInstanceId");
            string path = ReadString(p, "path");
            int steps = ReadIntNullable(p, "steps") ?? DefaultSteps;
            int holdFrames = ReadIntNullable(p, "holdFrames") ?? 0;

            var located = FairyGUINodeLocator.Locate(source, panelInstanceId, path);
            if (located.State != null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", located.State)));
            var obj = located.Node.Unwrap();
            if (obj == null)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));

            Vector2 center = FairyGUIGesturePlayer.CenterOf(obj);
            // 展开路径点 (stage 坐标)
            List<Vector2> stagePath;
            if (p != null && p.IsObject && p.ContainsKey("pathPoints") && p["pathPoints"].IsArray)
            {
                stagePath = new List<Vector2>();
                var arr = p["pathPoints"];
                for (int i = 0; i < arr.Count; i++)
                    stagePath.Add(center + new Vector2(ReadFloat(arr[i], "x"), ReadFloat(arr[i], "y")));
            }
            else if (p != null && p.IsObject && p.ContainsKey("pathShape") && p["pathShape"].IsObject)
            {
                var shape = p["pathShape"];
                string kind = shape.ContainsKey("shape") && shape["shape"].IsString ? (string)shape["shape"] : "circle";
                float radius = ReadFloat(shape, "radius");
                int segments = shape.ContainsKey("segments") && shape["segments"].IsInt ? (int)shape["segments"] : DefaultCircleSegments;
                stagePath = new List<Vector2>();
                if (kind == "circle")
                    for (int i = 1; i <= segments; i++)
                    {
                        float a = (float)(2.0 * Math.PI * i / segments);
                        stagePath.Add(center + new Vector2(radius * Mathf.Cos(a), radius * Mathf.Sin(a)));
                    }
                else
                    return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                        ("state", "error"), ("errorCode", "invalid_shape")));
            }
            else if (p != null && p.IsObject && p.ContainsKey("to") && p["to"].IsObject)
            {
                var to = p["to"];
                Vector2 endCenter;
                if (to.ContainsKey("toPath") && to["toPath"].IsString)
                {
                    var toLoc = FairyGUINodeLocator.Locate(source, panelInstanceId, (string)to["toPath"]);
                    var toObj = toLoc.State == null ? toLoc.Node.Unwrap() : null;
                    if (toObj == null)
                        return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "not_found")));
                    endCenter = FairyGUIGesturePlayer.CenterOf(toObj);
                }
                else
                {
                    endCenter = center + new Vector2(ReadFloat(to, "dx"), ReadFloat(to, "dy"));
                }
                stagePath = new List<Vector2>();
                for (int i = 1; i <= steps; i++)
                    stagePath.Add(Vector2.Lerp(center, endCenter, (float)i / steps));
            }
            else
            {
                // 无路径参数: 原地按住 (纯长按) —— 需 holdFrames > 0 才有意义
                stagePath = new List<Vector2>();
            }

            var startScreen = FairyGUIGesturePlayer.StageToScreen(center, stageInput.StageSize);
            var screenPath = new List<Vector2>(stagePath.Count);
            foreach (var sp in stagePath)
                screenPath.Add(FairyGUIGesturePlayer.StageToScreen(sp, stageInput.StageSize));

            var player = new FairyGUIGesturePlayer(stageInput, stepper, MaxFrames);
            bool ok = await player.PlayPath(startScreen, screenPath, holdFrames);
            if (!ok)
                return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(("state", "timeout")));
            return JsonRpcResponse.FromSuccess(request.Id, JsonRpcSerializer.Object(
                ("state", "ok"), ("pointsPlayed", screenPath.Count)));
        }

        private static float ReadFloat(JsonData o, string key)
        {
            if (o == null || !o.IsObject || !o.ContainsKey(key)) return 0f;
            var v = o[key];
            if (v.IsDouble) return (float)(double)v;
            if (v.IsInt) return (int)v;
            return 0f;
        }
        private static int? ReadIntNullable(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsInt ? (int?)(int)p[key] : null;
        private static string ReadString(JsonData p, string key)
            => p != null && p.IsObject && p.ContainsKey(key) && p[key].IsString ? (string)p[key] : null;
    }
}

using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands
{
    // registry 内部合成的同步组门面。Method = 组路由 key。
    internal sealed class GroupDispatchCommand : IRpcCommand
    {
        private readonly RpcGroupDefinition def;
        private readonly IReadOnlyDictionary<string, IGroupedCommand> actions;
        private readonly RpcToolDescriptor descriptor;

        public GroupDispatchCommand(
            RpcGroupDefinition def,
            IReadOnlyDictionary<string, IGroupedCommand> actions,
            RpcToolDescriptor descriptor)
        {
            this.def = def;
            this.actions = actions;
            this.descriptor = descriptor;
        }

        public string Method => def.Group;
        public RpcToolDescriptor Descriptor => descriptor;

        public JsonRpcResponse Handle(JsonRpcRequest request)
        {
            string action = GroupDispatchSupport.ReadAction(request.Params);
            if (action == null || !actions.TryGetValue(action, out var child))
            {
                return GroupDispatchSupport.InvalidAction(request.Id, def.Group, action, actions.Keys);
            }
            return child.Handle(request);
        }
    }

    internal static class GroupDispatchSupport
    {
        public static string ReadAction(JsonData p)
            => p != null && p.IsObject && p.ContainsKey("action") && p["action"].IsString
                ? (string)p["action"] : null;

        public static JsonRpcResponse InvalidAction(
            string id, string group, string action, IEnumerable<string> validActions)
        {
            var valid = new JsonData();
            valid.SetJsonType(JsonType.Array);
            foreach (var a in validActions) { valid.Add(a); }
            string message = action == null
                ? "missing 'action' for tool group " + group
                : "unknown action '" + action + "' for tool group " + group;
            return JsonRpcResponse.FromError(id, new JsonRpcError(
                JsonRpcErrorCodes.InvalidParams, message,
                JsonRpcSerializer.Object(
                    ("errorCode", "invalid_params"),
                    ("action", action ?? string.Empty),
                    ("validActions", valid))));
        }

        // 扁平合并 schema (spec 11A): action 枚举 + 各子命令 properties 平铺 + description 引导。
        // orderedMembers 决定 action 枚举顺序 (注册顺序)。
        public static RpcToolDescriptor BuildMergedDescriptor(
            RpcGroupDefinition def, IList<IGroupedCommand> orderedMembers)
        {
            var actionEnum = new JsonData();
            actionEnum.SetJsonType(JsonType.Array);

            var mergedProps = JsonRpcSerializer.Object();
            // propName -> 使用它的 action 列表 (拼进 description)
            var usedBy = new Dictionary<string, List<string>>();

            foreach (var member in orderedMembers)
            {
                actionEnum.Add(member.Action);
                JsonData childSchema = member.Descriptor.InputSchema;
                if (childSchema == null || !childSchema.IsObject ||
                    !childSchema.ContainsKey("properties") || !childSchema["properties"].IsObject)
                {
                    continue;
                }
                JsonData childProps = childSchema["properties"];
                foreach (var key in new List<string>(childProps.Keys))
                {
                    if (key == "action")
                    {
                        throw new System.InvalidOperationException(
                            "Sub-command '" + member.Action + "' in group '" + def.Group +
                            "' declares a parameter named 'action', which is reserved as the dispatch key. Rename the parameter.");
                    }
                    JsonData propDef = childProps[key];
                    if (!mergedProps.ContainsKey(key))
                    {
                        mergedProps[key] = propDef;
                        usedBy[key] = new List<string> { member.Action };
                    }
                    else
                    {
                        // 同名参数类型必须一致, 否则注册期报错 (fail fast)。
                        string existingType = TypeOf(mergedProps[key]);
                        string incomingType = TypeOf(propDef);
                        if (existingType != null && incomingType != null && existingType != incomingType)
                        {
                            throw new System.InvalidOperationException(
                                "Group '" + def.Group + "' has conflicting types for property '" + key +
                                "': " + existingType + " vs " + incomingType);
                        }
                        usedBy[key].Add(member.Action);
                    }
                }
            }

            // 给每个非 action 参数追加 action 引导。
            foreach (var kv in usedBy)
            {
                JsonData propDef = mergedProps[kv.Key];
                string note = " (used by action: " + string.Join(", ", kv.Value.ToArray()) + ")";
                string existing = propDef.IsObject && propDef.ContainsKey("description") &&
                                  propDef["description"].IsString ? (string)propDef["description"] : null;
                propDef["description"] = string.IsNullOrEmpty(existing) ? note.Trim() : existing + note;
            }

            var actionProp = JsonRpcSerializer.Object(
                ("type", "string"),
                ("description", "Which operation to perform; selects the sub-command that handles this call."));
            actionProp["enum"] = actionEnum;
            mergedProps["action"] = actionProp;

            var required = new JsonData();
            required.SetJsonType(JsonType.Array);
            required.Add("action");

            var inputSchema = JsonRpcSerializer.Object(
                ("type", "object"), ("additionalProperties", false));
            inputSchema["properties"] = mergedProps;
            inputSchema["required"] = required;

            return new RpcToolDescriptor
            {
                Name = def.ToolName,
                RpcMethod = def.Group,
                Title = def.Title,
                Description = def.Description,
                Completion = def.Completion,
                FailureMode = def.FailureMode,
                InputSchema = inputSchema,
                Annotations = def.Annotations
            };
        }

        private static string TypeOf(JsonData propDef)
            => propDef != null && propDef.IsObject && propDef.ContainsKey("type") && propDef["type"].IsString
                ? (string)propDef["type"] : null;
    }

    // registry 内部合成的异步组门面。落在 RpcConnectionLoop 的 IAsyncRpcCommand 分支。
    internal sealed class AsyncGroupDispatchCommand : IAsyncRpcCommand
    {
        private readonly RpcGroupDefinition def;
        private readonly IReadOnlyDictionary<string, IGroupedCommand> actions;
        private readonly RpcToolDescriptor descriptor;

        public AsyncGroupDispatchCommand(
            RpcGroupDefinition def,
            IReadOnlyDictionary<string, IGroupedCommand> actions,
            RpcToolDescriptor descriptor)
        {
            this.def = def;
            this.actions = actions;
            this.descriptor = descriptor;
        }

        public string Method => def.Group;
        public RpcToolDescriptor Descriptor => descriptor;

        public JsonRpcResponse Handle(JsonRpcRequest request)
            => JsonRpcResponse.FromError(request.Id, new JsonRpcError(
                JsonRpcErrorCodes.InternalError, "async command; use HandleAsync",
                JsonRpcSerializer.Object(("errorCode", "async_command"))));

        public async Cysharp.Threading.Tasks.UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            string action = GroupDispatchSupport.ReadAction(request.Params);
            if (action == null || !actions.TryGetValue(action, out var child))
            {
                return GroupDispatchSupport.InvalidAction(request.Id, def.Group, action, actions.Keys);
            }
            if (child is IAsyncRpcCommand asyncChild)
            {
                return await asyncChild.HandleAsync(request);
            }
            // 组内保证同质 (校验在 registry); 防御性地兜底同步子命令。
            return child.Handle(request);
        }
    }
}

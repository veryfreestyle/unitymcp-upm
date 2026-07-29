using System;
using System.Collections.Generic;
using LitJson;

namespace VeryFS.UnityMCP.Editor.Commands
{
    public sealed class RpcCommandRegistry
    {
        private readonly Dictionary<string, IRpcCommand> byMethod = new Dictionary<string, IRpcCommand>();
        private readonly List<RpcToolDescriptor> descriptors = new List<RpcToolDescriptor>();

        // 组登记 (注册期填充)。groupOrder 保留 RegisterGroup 调用顺序供工具列表稳定输出。
        private readonly List<RpcGroupDefinition> groupOrder = new List<RpcGroupDefinition>();
        private readonly Dictionary<string, RpcGroupDefinition> groupDefs =
            new Dictionary<string, RpcGroupDefinition>();
        private readonly Dictionary<string, List<IGroupedCommand>> groupMembers =
            new Dictionary<string, List<IGroupedCommand>>();

        private bool built;

        public void RegisterGroup(RpcGroupDefinition def)
        {
            EnsureNotBuilt();
            if (def == null) { throw new ArgumentNullException(nameof(def)); }
            if (string.IsNullOrEmpty(def.Group)) { throw new InvalidOperationException("RpcGroupDefinition.Group is required."); }
            if (string.IsNullOrEmpty(def.ToolName)) { throw new InvalidOperationException("RpcGroupDefinition.ToolName is required."); }
            if (groupDefs.ContainsKey(def.Group))
            {
                throw new InvalidOperationException("RPC group already registered: " + def.Group);
            }
            groupDefs[def.Group] = def;
            groupOrder.Add(def);
        }

        public void Register(IRpcCommand command)
        {
            EnsureNotBuilt();
            if (command == null) { throw new ArgumentNullException(nameof(command)); }
            if (command is IGroupedCommand grouped)
            {
                if (string.IsNullOrEmpty(grouped.Group)) { throw new InvalidOperationException("IGroupedCommand.Group is required."); }
                if (string.IsNullOrEmpty(grouped.Action)) { throw new InvalidOperationException("IGroupedCommand.Action is required."); }
                if (!groupMembers.TryGetValue(grouped.Group, out var members))
                {
                    members = new List<IGroupedCommand>();
                    groupMembers[grouped.Group] = members;
                }
                members.Add(grouped);
                return;
            }

            if (byMethod.ContainsKey(command.Method))
            {
                throw new InvalidOperationException(
                    "RPC command method already registered: " + command.Method);
            }
            byMethod[command.Method] = command;
            descriptors.Add(command.Descriptor);
        }

        public bool TryGet(string method, out IRpcCommand command)
        {
            EnsureBuilt();
            return byMethod.TryGetValue(method, out command);
        }

        public JsonData BuildToolsArray()
        {
            EnsureBuilt();
            var array = new JsonData();
            array.SetJsonType(JsonType.Array);
            foreach (var descriptor in descriptors)
            {
                array.Add(descriptor.ToJson());
            }
            return array;
        }

        private void EnsureNotBuilt()
        {
            if (built)
            {
                throw new InvalidOperationException(
                    "RpcCommandRegistry is already built; register all commands/groups before first access.");
            }
        }

        // 首次访问时把每个已登记组合成一个 dispatch 命令。fail fast 校验全在此。
        private void EnsureBuilt()
        {
            if (built) { return; }

            // 子命令引用了未定义的组 -> 报错。
            foreach (var kv in groupMembers)
            {
                if (!groupDefs.ContainsKey(kv.Key))
                {
                    throw new InvalidOperationException(
                        "Grouped command references undefined group: " + kv.Key);
                }
            }

            // Build all dispatches into locals first so partial failures leave byMethod/descriptors intact.
            var newEntries = new List<(string method, IRpcCommand dispatch, RpcToolDescriptor descriptor)>();
            foreach (var def in groupOrder)
            {
                if (!groupMembers.TryGetValue(def.Group, out var members) || members.Count == 0)
                {
                    throw new InvalidOperationException("Group defined but has no commands: " + def.Group);
                }

                var actionMap = new Dictionary<string, IGroupedCommand>();
                bool anyAsync = false;
                bool anySync = false;
                foreach (var member in members)
                {
                    if (actionMap.ContainsKey(member.Action))
                    {
                        throw new InvalidOperationException(
                            "Duplicate action '" + member.Action + "' in group " + def.Group);
                    }
                    actionMap[member.Action] = member;
                    if (member is IAsyncRpcCommand) { anyAsync = true; } else { anySync = true; }
                }
                if (anyAsync && anySync)
                {
                    throw new InvalidOperationException(
                        "Group '" + def.Group + "' mixes sync and async commands; a group must be all-sync or all-async.");
                }

                var descriptor = GroupDispatchSupport.BuildMergedDescriptor(def, members);

                IRpcCommand dispatch = anyAsync
                    ? (IRpcCommand)new AsyncGroupDispatchCommand(def, actionMap, descriptor)
                    : new GroupDispatchCommand(def, actionMap, descriptor);

                // Check collision against existing independent commands before committing.
                if (byMethod.ContainsKey(def.Group))
                {
                    throw new InvalidOperationException(
                        "Group route key collides with an existing command method: " + def.Group);
                }

                newEntries.Add((def.Group, dispatch, descriptor));
            }

            // Only write to shared state after all groups have been validated successfully.
            foreach (var (method, dispatch, descriptor) in newEntries)
            {
                byMethod[method] = dispatch;
                descriptors.Add(descriptor);
            }

            built = true;
        }
    }
}

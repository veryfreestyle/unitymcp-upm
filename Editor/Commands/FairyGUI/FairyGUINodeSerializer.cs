using System.Text;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // Walks an IUINode tree into JSON with depth limit and a UTF-8 byte budget.
    // Optional keys are omitted when empty (LitJson has no JSON-null).
    public sealed class FairyGUINodeSerializer
    {
        public const long DefaultBudgetBytes = 8_384_512; // 8 MiB - 4 KiB slack

        private readonly long budgetBytes;
        private long usedBytes;
        private readonly IWidgetStateReader widgetReader;

        public FairyGUINodeSerializer(long budgetBytes) : this(budgetBytes, null) { }

        public FairyGUINodeSerializer(long budgetBytes, IWidgetStateReader widgetReader)
        {
            this.budgetBytes = budgetBytes;
            this.widgetReader = widgetReader;
        }

        public bool Truncated { get; private set; }

        // Returns the node JSON, or null when the budget is exhausted (Truncated set).
        public JsonData SerializeNode(IUINode node, int remainingDepth)
        {
            var obj = JsonRpcSerializer.Object(("name", node.Name), ("type", node.TypeName));
            if (node.Text != null)
            {
                obj["text"] = node.Text;
            }
            obj["visible"] = node.Visible;
            obj["grayed"] = node.Grayed;
            obj["x"] = (double)node.X;
            obj["y"] = (double)node.Y;
            obj["width"] = (double)node.Width;
            obj["height"] = (double)node.Height;
            if (node.GameObjectInstanceId.HasValue)
            {
                obj["gameObjectInstanceId"] = node.GameObjectInstanceId.Value;
            }

            if (widgetReader != null)
            {
                var widgetState = widgetReader.ReadWidgetState(node);
                if (widgetState != null && widgetState.IsObject)
                {
                    foreach (var key in widgetState.Keys)
                    {
                        obj[key] = widgetState[key];
                    }
                }
            }

            if (node.IsComponent)
            {
                if (node.Controllers.Count > 0)
                {
                    var controllers = new JsonData();
                    controllers.SetJsonType(JsonType.Array);
                    foreach (var c in node.Controllers)
                    {
                        var cj = JsonRpcSerializer.Object(
                            ("name", c.Name), ("selectedIndex", c.SelectedIndex), ("pageCount", c.PageCount));
                        if (c.SelectedPage != null)
                        {
                            cj["selectedPage"] = c.SelectedPage;
                        }
                        controllers.Add(cj);
                    }
                    obj["controllers"] = controllers;
                }
                if (node.Transitions.Count > 0)
                {
                    var transitions = new JsonData();
                    transitions.SetJsonType(JsonType.Array);
                    foreach (var t in node.Transitions)
                    {
                        transitions.Add(JsonRpcSerializer.Object(
                            ("name", t.Name), ("playing", t.Playing), ("totalDuration", t.TotalDuration)));
                    }
                    obj["transitions"] = transitions;
                }
            }

            usedBytes += Encoding.UTF8.GetByteCount(JsonMapper.ToJson(obj));
            if (usedBytes > budgetBytes)
            {
                Truncated = true;
                return null;
            }

            if (node.IsComponent && remainingDepth > 0)
            {
                var children = new JsonData();
                children.SetJsonType(JsonType.Array);
                foreach (var child in node.Children)
                {
                    var childJson = SerializeNode(child, remainingDepth - 1);
                    if (childJson == null)
                    {
                        Truncated = true;
                        break;
                    }
                    children.Add(childJson);
                }
                if (children.Count > 0)
                {
                    obj["children"] = children;
                }
            }
            return obj;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    // Reads FairyGUI's live GRoot without triggering Stage.Instantiate() (which the
    // public GRoot.inst getter would do). Read-only: returns null outside play mode
    // or when no GRoot exists yet.
    public sealed class FairyGUIUITreeSource : IUITreeSource
    {
        private static readonly FieldInfo InstField =
            typeof(GRoot).GetField("_inst", BindingFlags.NonPublic | BindingFlags.Static);

        public bool IsPlaying => EditorApplication.isPlaying;

        public IUINode GetRoot()
        {
            var inst = InstField?.GetValue(null) as GRoot;
            return inst == null ? null : new GObjectNodeAdapter(inst);
        }
    }

    // Wraps a FairyGUI GObject as an IUINode. Children are built one level at a time
    // (lazy per access) so the serializer's depth limit bounds traversal cost.
    internal sealed class GObjectNodeAdapter : IUINode
    {
        private readonly GObject obj;

        public GObjectNodeAdapter(GObject obj)
        {
            this.obj = obj;
        }

        public string Name => obj.name;
        public string TypeName => obj.GetType().Name;
        public string Text => obj.text; // GObject.text is virtual; base returns null
        public bool Visible => obj.visible;
        public bool Grayed => obj.grayed;
        public float X => obj.x;
        public float Y => obj.y;
        public float Width => obj.width;
        public float Height => obj.height;

        public int? GameObjectInstanceId
        {
            get
            {
                var display = obj.displayObject;
                if (display != null && display.gameObject != null)
                {
                    return display.gameObject.GetInstanceID();
                }
                return null;
            }
        }

        public bool IsComponent => obj is GComponent;

        public IReadOnlyList<IUINode> Children
        {
            get
            {
                if (obj is GComponent gc)
                {
                    var list = new List<IUINode>(gc.numChildren);
                    for (int i = 0; i < gc.numChildren; i++)
                    {
                        list.Add(new GObjectNodeAdapter(gc.GetChildAt(i)));
                    }
                    return list;
                }
                return Array.Empty<IUINode>();
            }
        }

        public IReadOnlyList<UIControllerInfo> Controllers
        {
            get
            {
                if (obj is GComponent gc && gc.Controllers.Count > 0)
                {
                    var list = new List<UIControllerInfo>(gc.Controllers.Count);
                    foreach (var c in gc.Controllers)
                    {
                        list.Add(new UIControllerInfo(c.name, c.selectedIndex, c.selectedPage, c.pageCount));
                    }
                    return list;
                }
                return Array.Empty<UIControllerInfo>();
            }
        }

        public IReadOnlyList<UITransitionInfo> Transitions
        {
            get
            {
                if (obj is GComponent gc && gc.Transitions.Count > 0)
                {
                    var list = new List<UITransitionInfo>(gc.Transitions.Count);
                    foreach (var t in gc.Transitions)
                    {
                        list.Add(new UITransitionInfo(t.name, t.playing, t.totalDuration));
                    }
                    return list;
                }
                return Array.Empty<UITransitionInfo>();
            }
        }

        public GObject Unwrap() => obj;
    }
}

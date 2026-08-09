using System.Collections.Generic;
using FairyGUI;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public readonly struct FocusPath
    {
        public FocusPath(bool found, string path, int? panelInstanceId, GObject target)
        {
            Found = found;
            Path = path;
            PanelInstanceId = panelInstanceId;
            Target = target;
        }

        public bool Found { get; }
        public string Path { get; }
        public int? PanelInstanceId { get; }
        public GObject Target { get; }
    }

    /// <summary>
    /// 从当前焦点回推一条 FairyGUINodeLocator 吃得回去的 path。
    /// 只给 name + type 不可操作 —— AI 拿到"焦点是个叫 input 的 GTextInput"
    /// 没法据此定位。FairyGUINodeLocator 只有 path→node 一个方向, 所以这是新代码。
    /// </summary>
    public static class FairyGUIFocusPathResolver
    {
        public static FocusPath Resolve(IPanelSource source, GObject focused)
        {
            if (focused == null) { return new FocusPath(false, null, null, null); }

            IUINode groot = source.GetGRoot();
            if (groot != null && TryFrom(groot, focused, out string grootPath))
            {
                return new FocusPath(true, grootPath, null, focused);
            }

            // 焦点若在 panel 根下而非 GRoot 下, 回推时一并返回该 panel 的 panelInstanceId。
            foreach (PanelInfo panel in source.ListPanels())
            {
                IUINode root = source.GetPanelRoot(panel.InstanceId);
                if (root != null && TryFrom(root, focused, out string panelPath))
                {
                    return new FocusPath(true, panelPath, panel.InstanceId, focused);
                }
            }

            return new FocusPath(false, null, null, focused);
        }

        // 不能先 Find 再 PathTo(root, node, ...) 两阶段做: GObjectNodeAdapter.Children 每次访问都
        // new 一份全新的包装实例(FairyGUIUITreeSource.cs), 不缓存——Find 那一趟拿到的 node 引用,
        // 到 PathTo 内部 Walk 重新遍历 Children 时永远建不出同一个引用, ReferenceEquals 永远为假,
        // 于是真实焦点(root 本身以外的任何节点)一律解析成 Found=false。EditMode 用的 stub
        // (FairyGUIFocusPathResolverTests.cs 的 Node/FakeUINode)把 Children 缓存成固定列表,
        // 引用天然稳定, 这条 bug 在假节点上测不出来——直到这里用真 GObjectNodeAdapter 才现形。
        // 修法: 找到即同一趟顺手记路径, 不留到第二趟按引用对比。
        private static bool TryFrom(IUINode root, GObject focused, out string path)
        {
            var segments = new List<string>();
            if (!FindAndWalk(root, focused, segments)) { path = null; return false; }
            segments.Reverse();
            path = string.Join("/", segments.ToArray());
            return true;
        }

        private static bool FindAndWalk(IUINode current, GObject focused, List<string> segments)
        {
            if (ReferenceEquals(current.Unwrap(), focused)) { return true; }

            IReadOnlyList<IUINode> children = current.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (!FindAndWalk(children[i], focused, segments)) { continue; }
                segments.Add(SegmentFor(children, i));
                return true;
            }
            return false;
        }

        /// <summary>
        /// root 到 target 的路径。无名节点、或同层重名时用 [N] 索引段 ——
        /// 名字段在同层重名时会解析回第一个, 那不是我们找到的那个。
        ///
        /// 生产代码不再调用(Task 16 起真正的路径是 TryFrom + FindAndWalk, 一趟内边找边记
        /// 路径, 不靠 ReferenceEquals 比对两趟拿到的节点); 只有 EditMode 测试还在用它验
        /// SegmentFor/IsAmbiguousAsPathSegment 这套"选哪个段名"的规则本身(那部分逻辑两条
        /// 路径共用)。故意留 internal 而不是 public, 免得下一个调用方重新踩上 Find-then-
        /// Walk 两阶段撞 ReferenceEquals 的坑(review Minor: 那正是这个项目已经真的中过一次
        /// 的 bug 形状)。
        /// </summary>
        internal static bool PathTo(IUINode root, IUINode target, out string path)
        {
            var segments = new List<string>();
            if (!Walk(root, target, segments)) { path = null; return false; }
            segments.Reverse();
            path = string.Join("/", segments.ToArray());
            return true;
        }

        private static bool Walk(IUINode current, IUINode target, List<string> segments)
        {
            if (ReferenceEquals(current, target)) { return true; }

            IReadOnlyList<IUINode> children = current.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (!Walk(children[i], target, segments)) { continue; }
                segments.Add(SegmentFor(children, i));
                return true;
            }
            return false;
        }

        private static string SegmentFor(IReadOnlyList<IUINode> siblings, int index)
        {
            string name = siblings[index].Name;
            if (string.IsNullOrEmpty(name) || IsAmbiguousAsPathSegment(name)) { return "[" + index + "]"; }

            for (int i = 0; i < siblings.Count; i++)
            {
                if (i != index && siblings[i].Name == name) { return "[" + index + "]"; }
            }
            return name;
        }

        // 精确复刻 FairyGUINodeLocator.MatchSegment 判「这是哪种段」的那几行 (不是猜它的语法) ——
        // 名字一旦落进那几个分支, 就再也不会退回按名字匹配, 原样吐出去这个名字永远喂不对:
        //   - 含 '/': FairyGUINodeLocator.Locate 先把整条 path 按 '/' 切开, 名字里的 '/' 会被
        //     切成两段, 喂回去时找不到 (segment_not_found)。
        //   - 形如 "[...]" (长度 > 2, 首尾方括号): MatchSegment 一眼当成索引选择器 —— 数字解析
        //     成功就悄悄定位到那个索引的兄弟 (错误定位, 不是干净失败), 解析失败也不会退回按名字
        //     匹配 (同样喂不对)。
        //   - 形如 "#..." (长度 > 1, 首字符 '#'): 同理当成 gameObjectInstanceId 选择器拦掉,
        //     不退回按名字匹配。
        private static bool IsAmbiguousAsPathSegment(string name)
        {
            if (name.IndexOf('/') >= 0) { return true; }
            if (name.Length > 2 && name[0] == '[' && name[name.Length - 1] == ']') { return true; }
            if (name.Length > 1 && name[0] == '#') { return true; }
            return false;
        }
    }
}

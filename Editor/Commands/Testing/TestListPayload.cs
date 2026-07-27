using System;
using System.Collections.Generic;
using LitJson;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // ITestNode 树 -> assemblies 载荷的纯函数投影。脱离 Unity 运行时可测。
    public static class TestListPayload
    {
        // 递归找 IsTestAssembly 节点, 取其名 (去 .dll 后缀) 配上本次查询的 mode。
        // 程序集节点下不会再有程序集, 命中即停, 不继续下钻。
        public static void ExtractAssemblies(ITestNode node, string testMode, List<TestAssemblyInfo> sink)
        {
            if (node == null)
            {
                return;
            }

            if (node.IsTestAssembly)
            {
                string name = node.Name ?? string.Empty;
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - 4);
                }

                if (!string.IsNullOrEmpty(name))
                {
                    sink.Add(new TestAssemblyInfo { Name = name, TestMode = testMode });
                }

                return;
            }

            if (node.HasChildren && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    ExtractAssemblies(child, testMode, sink);
                }
            }
        }

        // POCO 列表 -> { assemblies: [ { name, testMode } ] }。
        // 按 (name, testMode) 去重, 按 name 再 testMode 序数升序, 保证同一指令结果可复现。
        public static JsonData BuildResponse(IReadOnlyList<TestAssemblyInfo> assemblies)
        {
            var seen = new HashSet<string>();
            var deduped = new List<TestAssemblyInfo>();
            if (assemblies != null)
            {
                foreach (var info in assemblies)
                {
                    if (info == null || string.IsNullOrEmpty(info.Name))
                    {
                        continue;
                    }

                    string key = info.Name + "\0" + info.TestMode;
                    if (seen.Add(key))
                    {
                        deduped.Add(info);
                    }
                }
            }

            deduped.Sort((x, y) =>
            {
                int byName = string.CompareOrdinal(x.Name, y.Name);
                return byName != 0 ? byName : string.CompareOrdinal(x.TestMode, y.TestMode);
            });

            var array = new JsonData();
            array.SetJsonType(JsonType.Array);
            foreach (var info in deduped)
            {
                array.Add(JsonRpcSerializer.Object(("name", info.Name), ("testMode", info.TestMode)));
            }

            var result = JsonRpcSerializer.Object();
            result["assemblies"] = array;
            return result;
        }
    }
}

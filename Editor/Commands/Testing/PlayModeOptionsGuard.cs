using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.Testing
{
    // PlayMode 测试运行期间临时禁用 domain reload, 让 MCP 连接不断。
    //
    // 已知失真 (spec §8): 禁用 domain reload 后静态字段与静态构造不重置,
    // PlayMode 测试之间可能互相污染, 结果与手工跑 / CI 不一致。
    // 已知风险: enterPlayModeOptions 落在 ProjectSettings/EditorSettings.asset,
    // 该文件入版本库 —— Guard 失效会留脏 diff。故双层持久化原值:
    //   SessionState  抗 domain reload
    //   Library/ 标记文件  抗崩溃与强杀 (Library 默认 gitignore)
    [InitializeOnLoad]
    public static class PlayModeOptionsGuard
    {
        private const string KeyPending = "VeryFS.UnityMCP.PlayModeOptions.PendingRestore";
        private const string KeyEnabled = "VeryFS.UnityMCP.PlayModeOptions.OriginalEnabled";
        private const string KeyOptions = "VeryFS.UnityMCP.PlayModeOptions.OriginalOptions";

        private static readonly string MarkerPath =
            Path.Combine("Library", "VeryFreestyle.UnityMcp", "PlayModeOptionsBackup.txt");

        static PlayModeOptionsGuard()
        {
            // 上一轮运行被中断 (reload / 崩溃 / 强杀) 时补恢复, 但不在静态构造里做:
            // Restore 要靠 AssetDatabase.SaveAssets() 才能把 EditorSettings 落盘, 而
            // [InitializeOnLoad] 静态构造阶段 AssetDatabase 不保证已就绪 —— 在那里做, flush
            // 会静默失败, 而恢复本身又"看起来成功了", 于是被跟踪的 ProjectSettings 文件里
            // 留着 override 没人再管。delayCall 在第一帧跑, 那时 AssetDatabase 一定可用。
            EditorApplication.delayCall += RecoverIfPending;
            // 退出 Editor 前再兜一次。TestRunCommand 的 Fail 路径 (init 超时 / 运行中断 /
            // 零测试兜底) 不认识本 Guard —— 那些路径收尾的 PlayMode 运行不会调 Restore,
            // override 会一直留在被跟踪的 ProjectSettings/EditorSettings.asset 里。挂在
            // quitting 上, 至少在"关掉 Editor"这一刻再尽力恢复一次 (但不清标记, 见
            // RecoverBeforeQuit —— 退出时落盘是否真的生效无从验证)。
            // 故意不做 IPlayModeController/tracker 依赖: 分层上 Guard 不该认识命令层。
            EditorApplication.quitting += RecoverBeforeQuit;
        }

        private static void RecoverIfPending()
        {
            if (!IsPending)
            {
                return;
            }

            Restore();
        }

        // 退出路径故意与第一帧恢复不对称: 只恢复内存值, 不清标记。
        // FlushToDisk 能确认的只是"SaveAssets 没抛异常", 而它在 Editor 退出流程里到底还落不落盘
        // 无从验证 —— 一旦它静默 no-op 而我们照常清了标记, 被跟踪的
        // ProjectSettings/EditorSettings.asset 就带着 override 进版本库, 且再没有下一次重试
        // (进程都要结束了)。所以这里把"确认"留给下次加载的第一帧 RecoverIfPending: 它重复恢复
        // 一遍已经正确的值是无害的 (幂等赋值), 而只要有一次 flush 真的成功, 标记就会被清掉,
        // 不会永远留着。
        private static void RecoverBeforeQuit()
        {
            if (!IsPending)
            {
                return;
            }

            Restore(clearMarkerOnFlush: false);
        }

        public static bool IsPending
        {
            get
            {
                if (SessionState.GetBool(KeyPending, false))
                {
                    return true;
                }

                try
                {
                    return File.Exists(MarkerPath);
                }
                catch
                {
                    return false;
                }
            }
        }

        // 返回 true 表示确实改了设置, 调用方运行结束后必须调 Restore。
        public static bool Apply()
        {
            bool originalEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var originalOptions = EditorSettings.enterPlayModeOptions;
            bool reloadAlreadyDisabled =
                (originalOptions & EnterPlayModeOptions.DisableDomainReload) != 0;
            if (originalEnabled && reloadAlreadyDisabled)
            {
                return false;
            }

            Save(originalEnabled, originalOptions);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = originalOptions | EnterPlayModeOptions.DisableDomainReload;
            // Apply 侧 flush 失败不致命: 内存里的 override 已经生效 (play mode 读的就是它),
            // 原值也已经双层备份, Restore 还会再落一次盘。
            FlushToDisk();
            return true;
        }

        public static void Restore() => Restore(clearMarkerOnFlush: true);

        private static void Restore(bool clearMarkerOnFlush)
        {
            var backup = ReadBackup(out bool originalEnabled, out var originalOptions);
            if (backup == BackupState.Missing)
            {
                return;
            }

            if (backup == BackupState.Corrupt)
            {
                // 标记文件在但解析不出原值 = 没有可恢复的目标。留着它 IsPending 永远为真,
                // 之后每次跑测都白走一遍 Restore 且永远清不掉 (还会让 UnityTestRunner 误以为
                // 仍欠一次恢复)。所以直接清掉标记并出声, 让人去核对
                // ProjectSettings/EditorSettings.asset —— 宁可丢一次自动恢复, 不要卡死状态机。
                Debug.LogWarning(
                    "Unity MCP: play mode options backup is unreadable; dropping it. " +
                    "Check ProjectSettings/EditorSettings.asset for a leftover enterPlayModeOptions override.");
                Clear();
                return;
            }

            // 幂等: 恢复一遍已经正确的值只是重复赋值, 什么都不会坏 —— 退出路径把标记留给
            // 下次加载再确认一次, 靠的就是这一点。
            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
            // 只有真的落盘成功才清标记。flush 失败时内存已恢复, 但被跟踪的
            // ProjectSettings/EditorSettings.asset 还留着 override —— 标记留着才有下一次重试
            // (下次跑测的 RestoreIfOwed / 下次加载的第一帧 / 退出前的 quitting), 清掉就等于
            // 把这条脏改动永久留给版本库。退出路径连"flush 成功"都不敢信, 见 RecoverBeforeQuit。
            if (FlushToDisk() && clearMarkerOnFlush)
            {
                Clear();
            }
        }

        // 实测过 (batchmode + 磁盘读回校验): AssetDatabase.SaveAssetIfDirty(Object) 配 LoadAllAssetsAtPath
        // 拿到的正是同一个单例 (SerializedObject 读出的值确实是改过的值)、AssetDatabase.SaveAssetIfDirty(GUID)、
        // InternalEditorUtility.SaveToSerializedFileAndForget、AssetDatabase.ForceReserializeAssets ——
        // 四个候选全部不落盘, 改完立刻读文件内容纹丝不动。原因: 这几个 API 都走 AssetDatabase 的 GUID
        // 索引, 而 ProjectSettings/ 下的资源不在 Assets/ 里、没有 .meta, 根本不进那套索引。唯一实测有效的
        // 是 AssetDatabase.SaveAssets() —— 代价是它会把此刻所有脏资源一起落盘, 不止这一个文件, 但没有
        // 更窄的替代品。
        // 返回是否确实落盘成功 —— Restore 必须据此决定能不能清标记, 吞掉失败再清标记就等于
        // 把一条脏的 ProjectSettings 改动永久留下, 而两层持久化本来就是为了防这个。
        private static bool FlushToDisk()
        {
            try
            {
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception exception)
            {
                // 启动路径 (第一帧恢复) 和退出路径都会走到这里, 绝不能抛出去 ——
                // 否则会挂掉整个插件的启动 / 退出流程。
                Debug.LogWarning("Unity MCP: failed to flush EditorSettings to disk. " + exception.Message);
                return false;
            }
        }

        private static void Save(bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            SessionState.SetBool(KeyEnabled, originalEnabled);
            SessionState.SetInt(KeyOptions, (int)originalOptions);
            SessionState.SetBool(KeyPending, true);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
                File.WriteAllText(MarkerPath, $"{(originalEnabled ? 1 : 0)}\n{(int)originalOptions}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Unity MCP: failed to write play mode options marker. " + exception.Message);
            }
        }

        private static void Clear()
        {
            SessionState.SetBool(KeyPending, false);
            try
            {
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }
            }
            catch
            {
                // 清理失败不致命: 下次 Apply 会覆盖, Restore 幂等。
            }
        }

        // 三态而不是 bool: "没有备份"和"备份读不出来"的善后完全不同 —— 前者什么都不该做,
        // 后者必须把标记清掉, 否则 IsPending 永远为真 (见 Restore 的 Corrupt 分支)。
        private enum BackupState
        {
            Missing,
            Loaded,
            Corrupt
        }

        private static BackupState ReadBackup(out bool originalEnabled, out EnterPlayModeOptions originalOptions)
        {
            if (SessionState.GetBool(KeyPending, false))
            {
                originalEnabled = SessionState.GetBool(KeyEnabled, false);
                originalOptions = (EnterPlayModeOptions)SessionState.GetInt(KeyOptions, 0);
                return BackupState.Loaded;
            }

            originalEnabled = false;
            originalOptions = EnterPlayModeOptions.None;
            try
            {
                if (!File.Exists(MarkerPath))
                {
                    return BackupState.Missing;
                }

                string[] lines = File.ReadAllLines(MarkerPath);
                if (lines.Length < 2 ||
                    !int.TryParse(lines[0].Trim(), out int enabled) ||
                    !int.TryParse(lines[1].Trim(), out int options))
                {
                    return BackupState.Corrupt;
                }

                originalEnabled = enabled != 0;
                originalOptions = (EnterPlayModeOptions)options;
                return BackupState.Loaded;
            }
            catch
            {
                // 文件在但读不动 (权限 / 被占用) 同样没有可恢复的原值, 走 Corrupt 的清理路径。
                return BackupState.Corrupt;
            }
        }
    }
}

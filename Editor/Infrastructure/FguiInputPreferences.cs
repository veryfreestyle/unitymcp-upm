using UnityEditor;

namespace VeryFS.UnityMCP.Editor.Infrastructure
{
    /// <summary>
    /// fgui-input 的机器/项目级校准。这三项不进工具参数面: AI 永远只给
    /// speedScale: 1 和 delta: 3, 平台与分辨率差异由装它的人调一次。
    /// 键按项目根哈希隔离, 与 ScreenshotPreferences 同构。
    /// </summary>
    internal static class FguiInputPreferences
    {
        // 出厂 1000 px/s: 1136x640 下跨半屏约 0.6 秒, 且 @60fps 每帧位移 16.7px,
        // 跨一个 100px 控件仍有 6 帧, rollover 链有派发余地。
        public const float DefaultPointerSpeedBase = 1000f;
        public const float DefaultWheelScale = 1f;
        public const bool DefaultVisualizerEnabled = true;

        private const string SpeedKeyPrefix = "VeryFS.UnityMCP.FguiInput.PointerSpeedBase.";
        private const string WheelKeyPrefix = "VeryFS.UnityMCP.FguiInput.WheelScale.";
        private const string VisualizerKeyPrefix = "VeryFS.UnityMCP.FguiInput.Visualizer.";

        public static float LoadPointerSpeedBase(string projectRoot)
            => PositiveOrDefault(
                EditorPrefs.GetFloat(SpeedKeyPrefix + ProjectRootHash.Compute(projectRoot), DefaultPointerSpeedBase),
                DefaultPointerSpeedBase);

        public static void SavePointerSpeedBase(string projectRoot, float value)
            => EditorPrefs.SetFloat(SpeedKeyPrefix + ProjectRootHash.Compute(projectRoot), value);

        public static float LoadWheelScale(string projectRoot)
            => PositiveOrDefault(
                EditorPrefs.GetFloat(WheelKeyPrefix + ProjectRootHash.Compute(projectRoot), DefaultWheelScale),
                DefaultWheelScale);

        public static void SaveWheelScale(string projectRoot, float value)
            => EditorPrefs.SetFloat(WheelKeyPrefix + ProjectRootHash.Compute(projectRoot), value);

        public static bool LoadVisualizerEnabled(string projectRoot)
            => EditorPrefs.GetBool(VisualizerKeyPrefix + ProjectRootHash.Compute(projectRoot), DefaultVisualizerEnabled);

        public static void SaveVisualizerEnabled(string projectRoot, bool value)
            => EditorPrefs.SetBool(VisualizerKeyPrefix + ProjectRootHash.Compute(projectRoot), value);

        internal static void Delete(string projectRoot)
        {
            string hash = ProjectRootHash.Compute(projectRoot);
            EditorPrefs.DeleteKey(SpeedKeyPrefix + hash);
            EditorPrefs.DeleteKey(WheelKeyPrefix + hash);
            EditorPrefs.DeleteKey(VisualizerKeyPrefix + hash);
        }

        // 面板里手滑填 0、负数或非有限值(NaN/Infinity)都会让 MoveAtSpeed 抛
        // ArgumentOutOfRangeException 或产生不可预测的位移, 那是运行期才炸的坑;
        // 读的时候就兜回出厂值。
        private static float PositiveOrDefault(float value, float fallback)
            => value > 0f && !float.IsInfinity(value) ? value : fallback;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using LitJson;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    public readonly struct FguiInputMotion
    {
        public FguiInputMotion(bool byFrames, float speedScale, int steps)
        {
            ByFrames = byFrames;
            SpeedScale = speedScale;
            Steps = steps;
        }

        public bool ByFrames { get; }
        public float SpeedScale { get; }
        public int Steps { get; }
    }

    /// <summary>
    /// 单条命令的规模上限。两类各自校验: 墙钟类的时长与帧率无关, 命令开始时算得出;
    /// 帧类的墙钟时长根本预估不出来(同一个帧数在不同 targetFrameRate 的场景里跨 30 倍
    /// 以上), 只能限帧数。
    /// </summary>
    public sealed class FguiInputBudget
    {
        public const float MaxWallClockMs = 30000f;
        public const int MaxFrames = 1800;   // ≈ 60fps 下 30 秒

        private float ms;
        private long frames;

        public void AddMs(float value) { if (value > 0f) { ms += value; } }

        // long, 不是 int: 大多数调用方传的是已经校验过上限的小整数(steps/frames 字段本身),
        // 但 type-text 的 framesPerChar * text.Length 是两个各自只校验了下限的量相乘,
        // 乘积能在 int32 里溢出折回负数(int.MaxValue ≈ 21 亿, 50000 × 50000 = 25 亿)。
        // 调用方必须用 long 算好乘积再传进来 —— 这里用 long 累加, 不会重蹈同样的溢出。
        public void AddFrames(long value) { if (value > 0) { frames += value; } }

        public string Violation()
        {
            if (ms > MaxWallClockMs)
            {
                return "Time-driven parts add up to " + ms.ToString("0", CultureInfo.InvariantCulture)
                    + " ms, over the " + MaxWallClockMs.ToString("0", CultureInfo.InvariantCulture)
                    + " ms limit for a single call. Split it into several calls, or raise speedScale.";
            }
            if (frames > MaxFrames)
            {
                return "Frame-driven parts add up to " + frames + " frames, over the "
                    + MaxFrames + " frame limit for a single call. Split it into several calls, "
                    + "or lower steps / framesPerChar.";
            }
            return null;
        }
    }

    /// <summary>13 个 action 共用的参数解析与校验。Error 非 null 时其余字段不可用。</summary>
    public sealed class FguiInputRequest
    {
        public const string MotionConflictMessage =
            "speedScale and steps are mutually exclusive: speedScale is time-driven "
            + "(1 = the project's configured base speed, so the same request takes the same "
            + "wall-clock time at any frame rate), steps is frame-driven (the whole displacement "
            + "is cut into N frames; 1 means teleport). Give one or neither.";

        private FguiInputRequest() { Warnings = new List<string>(); }

        public string Error { get; private set; }
        public string ErrorDetail { get; private set; }
        public List<string> Warnings { get; private set; }

        public bool HasPath { get; private set; }
        public string Path { get; private set; }
        public int? PanelInstanceId { get; private set; }
        public bool HasXy { get; private set; }
        public Vector2 Xy { get; private set; }
        public bool HasLocation => HasPath || HasXy;

        public FguiInputMotion Motion { get; private set; }
        public int Button { get; private set; }

        // speedScale: 1 = 面板配置的基准速度。命名带 Scale 是为了消除歧义 ——
        // 叫 speed 会诱导调用方写 speed: 1000 以为单位是 px/s。
        public float PixelsPerSecond(float pointerSpeedBase) => Motion.SpeedScale * pointerSpeedBase;

        // 出厂基准下 3× = 3000 px/s, @60fps 每帧位移 50px, 正好撞上 TouchInfo.Move() 的
        // clickCancelled 50 像素阈值。阈值从基准反推 (3000 / 基准 / 1.5), 不写死 2。
        public static float SpeedWarningThreshold(float pointerSpeedBase)
            => pointerSpeedBase <= 0f ? 2f : 3000f / pointerSpeedBase / 1.5f;

        public static FguiInputRequest Parse(JsonData p, float pointerSpeedBase)
        {
            var r = new FguiInputRequest();
            string error;

            string path = ReadString(p, "path", out error);
            if (error != null) { return r.Fail(error); }
            int? panelInstanceId = ReadIntNullable(p, "panelInstanceId", out error);
            if (error != null) { return r.Fail(error); }
            float? x = ReadFloatNullable(p, "x", out error);
            if (error != null) { return r.Fail(error); }
            float? y = ReadFloatNullable(p, "y", out error);
            if (error != null) { return r.Fail(error); }

            if (path != null && (x.HasValue || y.HasValue))
            {
                return r.Fail("Give either path (with optional panelInstanceId) or x/y, not both.");
            }
            if (x.HasValue != y.HasValue)
            {
                return r.Fail("x and y must be given together.");
            }
            if (path != null)
            {
                r.HasPath = true;
                r.Path = path;
                r.PanelInstanceId = panelInstanceId;
            }
            else if (x.HasValue)
            {
                r.HasXy = true;
                r.Xy = new Vector2(x.Value, y.Value);
            }

            int? steps = ReadIntNullable(p, "steps", out error);
            if (error != null) { return r.Fail(error); }
            float? speedScale = ReadFloatNullable(p, "speedScale", out error);
            if (error != null) { return r.Fail(error); }
            if (steps.HasValue && speedScale.HasValue)
            {
                return r.Fail(MotionConflictMessage);
            }
            if (steps.HasValue)
            {
                if (steps.Value < 1) { return r.Fail("steps must be at least 1 (1 means teleport)."); }
                r.Motion = new FguiInputMotion(true, 0f, steps.Value);
            }
            else
            {
                float scale = speedScale ?? 1f;
                if (scale <= 0f) { return r.Fail("speedScale must be greater than 0."); }
                r.Motion = new FguiInputMotion(false, scale, 0);

                float threshold = SpeedWarningThreshold(pointerSpeedBase);
                if (scale > threshold)
                {
                    r.Warnings.Add("speedScale " + scale.ToString("0.##", CultureInfo.InvariantCulture)
                        + " moves the pointer more than 33 px per frame at 60 fps, close to the 50 px "
                        + "clickCancelled threshold; drags and clicks are judged near that edge. "
                        + "Stay at or below " + threshold.ToString("0.##", CultureInfo.InvariantCulture) + ".");
                }
            }

            int? button = ReadIntNullable(p, "button", out error);
            if (error != null) { return r.Fail(error); }
            if (button.HasValue && (button.Value < 0 || button.Value > 2))
            {
                return r.Fail("button must be 0 (left), 1 (right) or 2 (middle).");
            }
            r.Button = button ?? 0;

            return r;
        }

        private FguiInputRequest Fail(string detail)
        {
            Error = "invalid_params";
            ErrorDetail = detail;
            return this;
        }

        // 三态契约(下面四个读取器一致): key 缺失 或 值是显式 JSON null -> 都算"没给",
        // 返回 null/fallback、error 置 null; key 有值但类型不对 -> error 非 null。
        // JSON null 走"没给"是因为 spec §3.2 把"两者都不给"列为定位/运动的合法状态之一,
        // 序列化器给未设字段吐 null 是常见形状, 不该被当成畸形值拒绝; 但类型给错通常是
        // 调用方拼错了参数, 悄悄当成"没给"会让错误在更深层、更难定位的地方才爆出来。
        public static int? ReadIntNullable(JsonData p, string key, out string error)
        {
            error = null;
            if (p == null || !p.IsObject || !p.ContainsKey(key)) { return null; }
            JsonData v = p[key];
            if (v == null) { return null; }
            if (v.IsInt) { return (int)v; }
            if (v.IsLong) { return (int)(long)v; }
            error = key + " must be an integer.";
            return null;
        }

        public static float? ReadFloatNullable(JsonData p, string key, out string error)
        {
            error = null;
            if (p == null || !p.IsObject || !p.ContainsKey(key)) { return null; }
            JsonData v = p[key];
            if (v == null) { return null; }
            if (v.IsDouble) { return (float)(double)v; }
            if (v.IsInt) { return (int)v; }
            if (v.IsLong) { return (long)v; }
            error = key + " must be a number.";
            return null;
        }

        public static string ReadString(JsonData p, string key, out string error)
        {
            error = null;
            if (p == null || !p.IsObject || !p.ContainsKey(key)) { return null; }
            JsonData v = p[key];
            if (v == null) { return null; }
            if (v.IsString) { return (string)v; }
            error = key + " must be a string.";
            return null;
        }

        public static bool ReadBool(JsonData p, string key, bool fallback, out string error)
        {
            error = null;
            if (p == null || !p.IsObject || !p.ContainsKey(key)) { return fallback; }
            JsonData v = p[key];
            if (v == null) { return fallback; }
            if (v.IsBoolean) { return (bool)v; }
            error = key + " must be a boolean.";
            return fallback;
        }

        // InputTextField 读的是 evt.ctrlOrCmd (Control 或 Command 任一成立),
        // 所以统一传 ["control"] 在 Windows 与 macOS 上都对。
        // 已知限制: command 在非 macOS 上 evt.command 恒为 false (InputEvent.cs:156-172)。
        public static bool TryReadModifiers(JsonData p, string key,
            out EventModifiers modifiers, out string error)
        {
            modifiers = EventModifiers.None;
            error = null;
            if (p == null || !p.IsObject || !p.ContainsKey(key)) { return true; }

            JsonData list = p[key];
            if (list == null) { return true; }   // 整个字段是 null: 等同没给, 没有 modifiers。
            if (!list.IsArray) { error = key + " must be an array of strings."; return false; }

            for (int i = 0; i < list.Count; i++)
            {
                // 数组元素里的 null 是畸形项(不是"没给这个字段"), 一律拒。
                JsonData item = list[i];
                if (item == null || !item.IsString)
                {
                    error = key + " must contain only strings.";
                    return false;
                }
                switch (((string)item).ToLowerInvariant())
                {
                    case "control": case "ctrl": modifiers |= EventModifiers.Control; break;
                    case "shift": modifiers |= EventModifiers.Shift; break;
                    case "alt": case "option": modifiers |= EventModifiers.Alt; break;
                    case "command": case "cmd": case "meta": modifiers |= EventModifiers.Command; break;
                    default:
                        error = "unknown modifier '" + (string)item
                            + "'; valid values are control, shift, alt, command.";
                        return false;
                }
            }
            return true;
        }

        public static bool TryReadKeyCode(JsonData p, string key, out KeyCode code, out string error)
        {
            code = KeyCode.None;
            // key 对这个读取器是必填项(不是可省略的定位/运动参数), 所以 ReadString
            // 把"没给"和"显式 null"都归一后, 这里再统一按"必须给"报错, 不需要区分来源。
            string raw = ReadString(p, key, out error);
            if (error != null) { return false; }
            if (raw == null) { error = key + " is required and must be a KeyCode name."; return false; }

            // 拒绝数字串: Enum.TryParse 会把 "13" 当成序号收下, 而调用方多半是在猜。
            bool numeric = true;
            for (int i = 0; i < raw.Length; i++)
            {
                if (!char.IsDigit(raw[i])) { numeric = false; break; }
            }
            if (numeric || !Enum.TryParse(raw, true, out code) || !Enum.IsDefined(typeof(KeyCode), code))
            {
                code = KeyCode.None;
                error = "'" + raw + "' is not a KeyCode name (for example Return, Escape, A, Delete).";
                return false;
            }
            return true;
        }
    }
}

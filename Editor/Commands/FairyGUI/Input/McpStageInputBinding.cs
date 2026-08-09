using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using UnityEngine;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 反射绑定 fork 的 StageInputSimulator / StageInputPlayer。
    ///
    /// 为什么反射而不是 asmdef versionDefines: versionDefines 只在 FairyGUI 以 UPM 包
    /// 形式安装时可靠, 把源码 vendor 到 Assets/ 的项目拿不到 define 会静默降级。
    /// 反射按类型存在与否判断, 覆盖全部安装形态。
    ///
    /// 部分绑定失败 = 整体失败, 由调用方降级 legacy, 不半生效。
    /// </summary>
    public sealed class McpStageInputBinding
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        private readonly Type styleType;
        private readonly MethodInfo startMethod;         // Start(StageInputMode, string, bool)
        private readonly object mouseMode;               // StageInputMode.Mouse (boxed)
        private readonly MethodInfo runMethod;           // Run(IEnumerator, Action<StageInputRunResult, Exception>)
        private readonly Delegate runCallback;           // 绑到 OnRunComplete<T> 的闭合委托
        private readonly MethodInfo cancelMethod;
        private readonly MethodInfo forceResetMethod;
        private readonly PropertyInfo activeProperty;
        private readonly PropertyInfo isRunningProperty;
        private readonly MethodInfo useDefaultVisualizerMethod;
        private readonly MethodInfo disableVisualizerMethod;
        private readonly MethodInfo clearVisualizerMethod;
        private readonly MethodInfo styleDefaultMethod;
        private readonly PropertyInfo mousePositionProperty; // StageInputSimulator.mousePosition
        private readonly MethodInfo disposeMethod;           // StageInputPlayer.Dispose()

        private readonly Dictionary<string, MethodInfo> playerMethods;

        private Action<string, Exception> pendingCallback;

        private McpStageInputBinding(Type styleType,
            MethodInfo startMethod, object mouseMode, MethodInfo runMethod,
            MethodInfo cancelMethod, MethodInfo forceResetMethod,
            PropertyInfo activeProperty, PropertyInfo isRunningProperty,
            MethodInfo useDefaultVisualizerMethod, MethodInfo disableVisualizerMethod,
            MethodInfo clearVisualizerMethod, MethodInfo styleDefaultMethod,
            PropertyInfo mousePositionProperty,
            MethodInfo disposeMethod, Dictionary<string, MethodInfo> playerMethods,
            Type runResultType)
        {
            this.styleType = styleType;
            this.startMethod = startMethod;
            this.mouseMode = mouseMode;
            this.runMethod = runMethod;
            this.cancelMethod = cancelMethod;
            this.forceResetMethod = forceResetMethod;
            this.activeProperty = activeProperty;
            this.isRunningProperty = isRunningProperty;
            this.useDefaultVisualizerMethod = useDefaultVisualizerMethod;
            this.disableVisualizerMethod = disableVisualizerMethod;
            this.clearVisualizerMethod = clearVisualizerMethod;
            this.styleDefaultMethod = styleDefaultMethod;
            this.mousePositionProperty = mousePositionProperty;
            this.disposeMethod = disposeMethod;
            this.playerMethods = playerMethods;

            // Run 的回调参数类型是 Action<StageInputRunResult, Exception>, 而 StageInputRunResult
            // 是 fork-only 枚举, MCP 侧不能引用。泛型方法 MakeGenericMethod(枚举类型) 后签名
            // 精确匹配, CreateDelegate 就能绑; 内部再按枚举名转成字符串回传。
            MethodInfo generic = typeof(McpStageInputBinding)
                .GetMethod("OnRunComplete", BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(runResultType);
            runCallback = Delegate.CreateDelegate(
                runMethod.GetParameters()[1].ParameterType, this, generic);
        }

        public static bool TryBind(out McpStageInputBinding binding, out string missingMember)
        {
            binding = null;
            missingMember = null;

            Assembly fgui = typeof(global::FairyGUI.Stage).Assembly;
            Type simulator = fgui.GetType("FairyGUI.StageInputSimulator");
            if (simulator == null) { missingMember = "FairyGUI.StageInputSimulator"; return false; }

            Type playerType = fgui.GetType("FairyGUI.StageInputPlayer");
            Type modeType = fgui.GetType("FairyGUI.StageInputMode");
            Type resultType = fgui.GetType("FairyGUI.StageInputRunResult");
            Type styleType = fgui.GetType("FairyGUI.InputVisualStyle");
            if (playerType == null) { missingMember = "FairyGUI.StageInputPlayer"; return false; }
            if (modeType == null) { missingMember = "FairyGUI.StageInputMode"; return false; }
            if (resultType == null) { missingMember = "FairyGUI.StageInputRunResult"; return false; }
            if (styleType == null) { missingMember = "FairyGUI.InputVisualStyle"; return false; }

            MethodInfo start = simulator.GetMethod("Start", PublicStatic,
                null, new[] { modeType, typeof(string), typeof(bool) }, null);
            MethodInfo run = simulator.GetMethod("Run", PublicStatic,
                null, new[] { typeof(IEnumerator), typeof(Action<,>).MakeGenericType(resultType, typeof(Exception)) }, null);
            MethodInfo cancel = simulator.GetMethod("Cancel", PublicStatic, null, Type.EmptyTypes, null);
            MethodInfo forceReset = simulator.GetMethod("ForceReset", PublicStatic, null, Type.EmptyTypes, null);
            PropertyInfo active = simulator.GetProperty("active", PublicStatic);
            PropertyInfo isRunning = simulator.GetProperty("isRunning", PublicStatic);
            MethodInfo useDefault = simulator.GetMethod("UseDefaultVisualizer", PublicStatic,
                null, new[] { typeof(Nullable<>).MakeGenericType(styleType) }, null);
            MethodInfo disable = simulator.GetMethod("DisableVisualizer", PublicStatic, null, Type.EmptyTypes, null);
            MethodInfo clear = simulator.GetMethod("ClearVisualizer", PublicStatic, null, Type.EmptyTypes, null);
            MethodInfo styleDefault = styleType.GetMethod("Default", PublicStatic, null, Type.EmptyTypes, null);
            // Public|Static, 不是 IStageInputSource.mousePosition(那是 instance 属性, 且未接管
            // 会话时 Stage.inputSource 指向真实鼠标, 读它会读错源, 见 review Finding 1)。
            PropertyInfo mousePosition = simulator.GetProperty("mousePosition", PublicStatic);
            MethodInfo dispose = playerType.GetMethod("Dispose", PublicInstance, null, Type.EmptyTypes, null);

            if (!Require(start, "StageInputSimulator.Start", ref missingMember)) return false;
            if (!Require(run, "StageInputSimulator.Run", ref missingMember)) return false;
            if (!Require(cancel, "StageInputSimulator.Cancel", ref missingMember)) return false;
            if (!Require(forceReset, "StageInputSimulator.ForceReset", ref missingMember)) return false;
            if (!Require(active, "StageInputSimulator.active", ref missingMember)) return false;
            if (!RequirePropertyType(active, typeof(bool), "StageInputSimulator.active", ref missingMember)) return false;
            if (!Require(isRunning, "StageInputSimulator.isRunning", ref missingMember)) return false;
            if (!RequirePropertyType(isRunning, typeof(bool), "StageInputSimulator.isRunning", ref missingMember)) return false;
            if (!Require(useDefault, "StageInputSimulator.UseDefaultVisualizer", ref missingMember)) return false;
            if (!Require(disable, "StageInputSimulator.DisableVisualizer", ref missingMember)) return false;
            if (!Require(clear, "StageInputSimulator.ClearVisualizer", ref missingMember)) return false;
            if (!Require(styleDefault, "InputVisualStyle.Default", ref missingMember)) return false;
            if (!RequireReturnType(styleDefault, styleType, "InputVisualStyle.Default", ref missingMember)) return false;
            if (!Require(mousePosition, "StageInputSimulator.mousePosition", ref missingMember)) return false;
            if (!RequirePropertyType(mousePosition, typeof(Vector2), "StageInputSimulator.mousePosition", ref missingMember)) return false;
            if (!Require(dispose, "StageInputPlayer.Dispose", ref missingMember)) return false;

            var methods = new Dictionary<string, MethodInfo>();
            if (!BindPlayerMethods(playerType, methods, ref missingMember)) return false;

            object mouse;
            try { mouse = Enum.Parse(modeType, "Mouse"); }
            catch (ArgumentException) { missingMember = "StageInputMode.Mouse"; return false; }

            // OnRunComplete<TResult> 只按名字转成字符串回传(result.ToString()), 命令层的
            // McpRunOutcome.Completed 再按字面量 "Completed" 比对 —— 这条链路完全没有编译期
            // 类型检查撑着。StageInputMode.Mouse 校验过存在, StageInputRunResult 的三个值
            // 没有(review Minor), 万一 fork 重命名了枚举值, 探测器会报告"支持注入"但之后
            // 每一次 run 都会把 result.ToString() 得到一个 McpRunOutcome.Completed/Canceled
            // 认不出的名字, 静默全部判定失败。跟 Mouse 一样按名字校验, 不校验数值。
            if (!RequireEnumName(resultType, "Completed", ref missingMember)) return false;
            if (!RequireEnumName(resultType, "Canceled", ref missingMember)) return false;
            if (!RequireEnumName(resultType, "Faulted", ref missingMember)) return false;

            try
            {
                binding = new McpStageInputBinding(styleType, start, mouse, run,
                    cancel, forceReset, active, isRunning, useDefault, disable, clear,
                    styleDefault, mousePosition, dispose, methods, resultType);
            }
            catch (Exception ex)
            {
                missingMember = "StageInputSimulator.Run callback binding: " + ex.Message;
                return false;
            }
            return true;
        }

        // 逐个签名精确取。签名一变就绑不上, 而绑不上会整体降级 legacy —— 这正是我们要的:
        // 半绑定比全不绑定更难查。
        private static bool BindPlayerMethods(
            Type playerType, Dictionary<string, MethodInfo> methods, ref string missingMember)
        {
            var wanted = new (string name, Type[] args)[]
            {
                ("MoveTo",         new[] { typeof(Vector2), typeof(int) }),
                ("MoveAtSpeed",    new[] { typeof(Vector2), typeof(float) }),
                ("Click",          new[] { typeof(Vector2), typeof(int) }),
                ("Press",          new[] { typeof(Vector2), typeof(int) }),
                ("Release",        new[] { typeof(Vector2), typeof(int) }),
                ("Drag",           new[] { typeof(Vector2), typeof(Vector2), typeof(int), typeof(int), typeof(int), typeof(int) }),
                ("DragAtSpeed",    new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(int) }),
                ("SendKey",        new[] { typeof(KeyCode), typeof(EventModifiers) }),
                ("TypeText",       new[] { typeof(string), typeof(int) }),
                ("TypeTextAtRate", new[] { typeof(string), typeof(float) }),
                ("Scroll",         new[] { typeof(Vector2), typeof(float), typeof(EventModifiers) }),
                ("Step",           new[] { typeof(int) }),
                ("StepMs",         new[] { typeof(float) }),
                ("ReleaseHeld",    Type.EmptyTypes),
            };

            foreach (var entry in wanted)
            {
                MethodInfo m = playerType.GetMethod(entry.name, PublicInstance, null, entry.args, null);
                if (m == null || m.ReturnType != typeof(IEnumerator))
                {
                    missingMember = "StageInputPlayer." + entry.name;
                    return false;
                }
                methods[entry.name] = m;
            }
            return true;
        }

        private static bool Require(object member, string name, ref string missingMember)
        {
            if (member != null) { return true; }
            missingMember = name;
            return false;
        }

        private static bool RequireEnumName(Type enumType, string name, ref string missingMember)
        {
            foreach (string candidate in Enum.GetNames(enumType))
            {
                if (candidate == name) { return true; }
            }
            missingMember = enumType.Name + "." + name;
            return false;
        }

        // 签名存在但类型对不上(比如某个 fork 变体把 mousePosition 声明成 Vector3)也要按
        // "缺成员"一样整体失败, 而不是让 TryBind 放行、等到第一次真正读取时才炸出
        // InvalidCastException —— 那时探测器已经报告过"支持注入", 半生效比全不生效更难查。
        private static bool RequirePropertyType(
            PropertyInfo property, Type expected, string name, ref string missingMember)
        {
            if (property.PropertyType == expected) { return true; }
            missingMember = name;
            return false;
        }

        private static bool RequireReturnType(
            MethodInfo method, Type expected, string name, ref string missingMember)
        {
            if (method.ReturnType == expected) { return true; }
            missingMember = name;
            return false;
        }

        public bool Active => (bool)activeProperty.GetValue(null, null);
        public bool IsRunning => (bool)isRunningProperty.GetValue(null, null);

        // 读 StageInputSimulator.mousePosition(fork 新增的静态只读访问器, 转写
        // ScriptedInputSource.mousePosition), 不读 Stage.inputSource: 未接管会话
        // (active 为 false)时 Stage.inputSource 已经是 StageInputSimulator.Restore() 换回去的
        // _prevInputSource(通常是 UnityInputSource, 直通操作者真实物理指针), 经它转一手会
        // 读错源(review Finding 1)。脚本虚拟指针跨会话延续、不随 ResetAll 清零, 一条命令读一次,
        // 不在逐帧路径上, 用 GetValue 就够。
        public Vector2 CurrentPointerPosition => (Vector2)mousePositionProperty.GetValue(null, null);

        public McpStageInputPlayer Start(string label, bool syncMousePositionFromCurrent)
        {
            object instance = InvokeUnwrapped(startMethod, null,
                new object[] { mouseMode, label, syncMousePositionFromCurrent });
            var player = new McpStageInputPlayer { Instance = instance };
            player.MoveTo = Bind<Func<Vector2, int, IEnumerator>>(instance, "MoveTo");
            player.MoveAtSpeed = Bind<Func<Vector2, float, IEnumerator>>(instance, "MoveAtSpeed");
            player.Click = Bind<Func<Vector2, int, IEnumerator>>(instance, "Click");
            player.Press = Bind<Func<Vector2, int, IEnumerator>>(instance, "Press");
            player.Release = Bind<Func<Vector2, int, IEnumerator>>(instance, "Release");
            player.Drag = Bind<Func<Vector2, Vector2, int, int, int, int, IEnumerator>>(instance, "Drag");
            player.DragAtSpeed = Bind<Func<Vector2, Vector2, float, float, float, int, IEnumerator>>(instance, "DragAtSpeed");
            player.SendKey = Bind<Func<KeyCode, EventModifiers, IEnumerator>>(instance, "SendKey");
            player.TypeText = Bind<Func<string, int, IEnumerator>>(instance, "TypeText");
            player.TypeTextAtRate = Bind<Func<string, float, IEnumerator>>(instance, "TypeTextAtRate");
            player.Scroll = Bind<Func<Vector2, float, EventModifiers, IEnumerator>>(instance, "Scroll");
            player.Step = Bind<Func<int, IEnumerator>>(instance, "Step");
            player.StepMs = Bind<Func<float, IEnumerator>>(instance, "StepMs");
            player.ReleaseHeld = Bind<Func<IEnumerator>>(instance, "ReleaseHeld");
            return player;
        }

        private T Bind<T>(object instance, string name) where T : class
            => (T)(object)Delegate.CreateDelegate(typeof(T), instance, playerMethods[name]);

        public void Dispose(McpStageInputPlayer player)
        {
            if (player?.Instance == null) { return; }
            InvokeUnwrapped(disposeMethod, player.Instance, null);
        }

        public void ForceReset() => InvokeUnwrapped(forceResetMethod, null, null);

        public void Run(IEnumerator sequence, Action<string, Exception> onComplete)
        {
            // 先调用再挂 pendingCallback: Run 有四道门(sequence 为 null / 不在 Play 模式 /
            // 未 Start / 已有序列在跑), 任何一道没过都会抛异常、Invoke 不会真正启动协程。
            // 若挂在 Invoke 之前, 一次被拒绝的 Run 会让 pendingCallback 悬空到下一次调用才被
            // 覆盖或触发 —— 那时它对应的已经是上一次被拒的调用而不是当次, 回调会错配。
            InvokeUnwrapped(runMethod, null, new object[] { sequence, runCallback });
            pendingCallback = onComplete;
        }

        public void Cancel() => InvokeUnwrapped(cancelMethod, null, null);

        // MethodInfo.Invoke 把被调方法体里抛出的异常包进 TargetInvocationException, 调用方
        // 看到的永远是同一个外壳、读不到 fork 真正想说的话 —— 而 fork 这几个方法的异常消息是
        // 专门写给调用方看的诊断("已被会话 '<label>' 占用" / Run 的四道门分别说哪里错)。
        // 解包后用 ExceptionDispatchInfo 重新抛, 保留原始堆栈, 而不是 throw ex.InnerException
        // 那样把堆栈钉在这一行。
        private static object InvokeUnwrapped(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw; // 不可达, 满足编译器的返回路径分析。
            }
        }

        // 枚举值不按序号比对, 按名字。序号被重排时序号比对会静默认错,
        // 名字重命名则会明确地对不上任何一个已知值。
        private void OnRunComplete<TResult>(TResult result, Exception error)
        {
            Action<string, Exception> callback = pendingCallback;
            pendingCallback = null;
            callback?.Invoke(result == null ? null : result.ToString(), error);
        }

        public void UseDefaultVisualizer(IDictionary<string, object> styleOverrides)
        {
            if (styleOverrides == null || styleOverrides.Count == 0)
            {
                InvokeUnwrapped(useDefaultVisualizerMethod, null, new object[] { null });
                return;
            }

            // 必须从 Default() 起手改字段: struct 无字段初始化器, 漏写的字段会是
            // 全透明黑 + 尺寸 0 (P22 §5.10)。
            object boxed = InvokeUnwrapped(styleDefaultMethod, null, null);
            foreach (var kv in styleOverrides)
            {
                FieldInfo field = styleType.GetField(kv.Key, PublicInstance);
                if (field == null) { continue; }
                field.SetValue(boxed, ConvertTo(field.FieldType, kv.Value));
            }
            InvokeUnwrapped(useDefaultVisualizerMethod, null, new[] { boxed });
        }

        private static object ConvertTo(Type target, object value)
        {
            if (value == null) { return null; }
            if (target.IsInstanceOfType(value)) { return value; }
            if (target.IsEnum && value is string name) { return Enum.Parse(target, name, true); }
            return Convert.ChangeType(value, target);
        }

        public void DisableVisualizer() => InvokeUnwrapped(disableVisualizerMethod, null, null);
        public void ClearVisualizer() => InvokeUnwrapped(clearVisualizerMethod, null, null);
    }
}

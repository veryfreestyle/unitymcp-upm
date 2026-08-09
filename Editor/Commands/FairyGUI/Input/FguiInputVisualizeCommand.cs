using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Commands;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.FairyGUI
{
    /// <summary>
    /// 唯一不接管 session、不推帧的 action: 只是把可视化标记打开/关闭/清空。
    ///
    /// Task 3 评审裁定: McpStageInputBinding.UseDefaultVisualizer 对未知 styleOverrides
    /// 键悄悄 continue、对转换不了的值让 ConvertTo 直接抛 InvalidCastException, 两者都不在
    /// binding 那层修 —— 键白名单与类型校验放在这里, 因为这里才有 invalid_params 通道能把
    /// 错误报回调用方。白名单从 FairyGUI.InputVisualStyle 的真实字段反射取得(同 fork 装了
    /// 就是同一份签名, 不会跟 binding 各自维护一份不同步的键表), 不写死在这个文件里,
    /// 也不对 InputVisualStyle 加编译期引用(它在禁止直接引用的 fork-only 类型名单上)。
    /// </summary>
    public sealed class FguiInputVisualizeCommand : FguiInputCommandBase
    {
        public FguiInputVisualizeCommand(IPanelSource panelSource, IMcpStageInput input,
            McpStageInputSessionManager sessions, string projectRoot)
            : base(panelSource, input, sessions, projectRoot) { }

        public override string Method => RpcMethods.FairyGuiInputVisualize;
        public override string Action => "visualize";

        public override RpcToolDescriptor Descriptor => new RpcToolDescriptor
        {
            Name = "fgui-input-visualize",
            RpcMethod = RpcMethods.FairyGuiInputVisualize,
            Title = "FairyGUI / Input / Visualize",
            Description = "Draw or hide the injected pointer and touch markers. Drawing only; it produces no "
                + "input and runs no frames. The markers show up in Game View screenshots, which is how you "
                + "tell where a click landed. style overrides individual fields of the default look "
                + "(cursorShape, cursorColor, cursorSize, pressColor, touchRadius, ...); fields you omit keep "
                + "their defaults. clear wipes marks already drawn.",
            Completion = "response",
            FailureMode = "error",
            InputSchema = Schema(p =>
            {
                p["enabled"] = JsonRpcSerializer.Object(
                    ("type", "boolean"), ("description", "Required. Draw the markers or not."));
                p["style"] = JsonRpcSerializer.Object(
                    ("type", "object"), ("description", "Field-by-field overrides of the default look."));
                p["clear"] = JsonRpcSerializer.Object(
                    ("type", "boolean"), ("description", "Also wipe marks already drawn. Default false."));
            }),
            Annotations = JsonRpcSerializer.Object(("idempotentHint", true))
        };

        // 全程没有一步是异步的(UseDefaultVisualizer / DisableVisualizer / ClearVisualizer
        // 都是同步调用, 不像 click/wheel 那样要 await Input.RunAsync 推帧), 所以 body 不标
        // async —— 同 FguiInputSessionCommands.cs 顶上的注释, 每个分支显式包一层
        // UniTask.FromResult, 不为了凑 Func<UniTask<...>> 的签名硬套一个没有 await 的
        // async lambda(那样会撞 CS1998, 而且没有理由)。
        //
        // 但整段仍然进 Guarded: UseDefaultVisualizer/DisableVisualizer/ClearVisualizer 都
        // 经过 McpStageInputBinding 的反射调用, 四个都可能抛(比如 fork 内部状态异常) ——
        // 这批 action 里没有代码站在 Guarded 外面, 见 FguiInputCommandBase.Guarded 的注释。
        public override UniTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request)
        {
            return Guarded(request, () =>
            {
                JsonData p = request.Params;

                JsonData enabledValue = (p != null && p.IsObject && p.ContainsKey("enabled")) ? p["enabled"] : null;
                if (enabledValue == null || !enabledValue.IsBoolean)
                {
                    return UniTask.FromResult(
                        InvalidParams(request, "'enabled' is required and must be a boolean."));
                }
                bool enabled = (bool)enabledValue;

                string error;
                bool clear = FguiInputRequest.ReadBool(p, "clear", false, out error);
                if (error != null) { return UniTask.FromResult(InvalidParams(request, error)); }

                // style 只在 enabled 时会真正被用掉(DisableVisualizer 不吃样式), 但校验
                // 必须总是跑 —— 否则同一个拼错的键在 enabled:true 下报 invalid_params、
                // 在 enabled:false 下却静默放过(review Minor), 调用方两次拿到不一致的
                // 反馈。校验永远做, 只是转换出来的值在 enabled:false 时不会被使用。
                IDictionary<string, object> styleOverrides;
                if (!TryReadStyleOverrides(request, p, out styleOverrides, out JsonRpcResponse styleFailure))
                {
                    return UniTask.FromResult(styleFailure);
                }

                if (!Input.IsPlaying)
                {
                    return UniTask.FromResult(JsonRpcResponse.FromSuccess(request.Id,
                        JsonRpcSerializer.Object(("state", "not_playing"))));
                }

                if (enabled)
                {
                    // style 必须逐字段覆盖在 Default() 之上, 不能整体替换: fork 的
                    // InputVisualStyle 是 struct 且无字段初始化器, 漏写的字段会是
                    // 全透明黑 + 尺寸 0, 等于画了个看不见的东西(binding.UseDefaultVisualizer
                    // 自己从 Default() 起手改, 这里只需要把校验过的字段传过去)。
                    Input.UseDefaultVisualizer(styleOverrides);
                }
                else
                {
                    Input.DisableVisualizer();
                }
                if (clear) { Input.ClearVisualizer(); }

                return UniTask.FromResult(JsonRpcResponse.FromSuccess(request.Id,
                    JsonRpcSerializer.Object(("state", "ok"), ("enabled", enabled))));
            });
        }

        // ---- styleOverrides 的键白名单与类型校验。全部在把值交给 binding 之前完成,
        // 转换失败在这里就以 invalid_params 报出来, 不会漏到 binding.ConvertTo 里
        // 变成未接住的 InvalidCastException / ArgumentException。----

        private bool TryReadStyleOverrides(JsonRpcRequest request, JsonData p,
            out IDictionary<string, object> overrides, out JsonRpcResponse failure)
        {
            overrides = null;
            failure = null;

            if (p == null || !p.IsObject || !p.ContainsKey("style")) { return true; }
            JsonData style = p["style"];
            if (style == null) { return true; }   // 显式 JSON null 等同没给(三态约定)。
            if (!style.IsObject)
            {
                failure = InvalidParams(request, "'style' must be an object.");
                return false;
            }

            IDictionary<string, Type> fieldTypes = StyleFieldTypes;
            var result = new Dictionary<string, object>();
            foreach (string key in style.Keys)
            {
                Type fieldType;
                if (fieldTypes == null || !fieldTypes.TryGetValue(key, out fieldType))
                {
                    string known = fieldTypes == null
                        ? string.Empty
                        : " Valid fields: " + JoinFieldNames(fieldTypes) + ".";
                    failure = InvalidParams(request,
                        "'style." + key + "' is not a recognized visualizer style field." + known);
                    return false;
                }

                JsonData value = style[key];
                if (value == null) { continue; }   // 该字段显式 null: 等同没给, 保留默认值。

                object converted;
                string typeError;
                if (!TryConvertStyleValue(fieldType, value, out converted, out typeError))
                {
                    failure = InvalidParams(request, "'style." + key + "' " + typeError);
                    return false;
                }
                result[key] = converted;
            }

            overrides = result.Count == 0 ? null : result;
            return true;
        }

        // 逐字段按真实反射类型校验/转换。四种都覆盖到了就是 InputVisualStyle 现在的
        // 全部字段种类(bool / float / Color / 枚举); 万一 fork 以后加了新种类的字段,
        // 最后这条 default 分支报"不支持", 不会静默放过一个没人校验过的值类型。
        private static bool TryConvertStyleValue(
            Type fieldType, JsonData value, out object converted, out string typeError)
        {
            converted = null;
            typeError = null;

            if (fieldType == typeof(bool))
            {
                if (value.IsBoolean) { converted = (bool)value; return true; }
                typeError = "must be a boolean.";
                return false;
            }

            if (fieldType == typeof(float))
            {
                if (TryAsFloat(value, out float f)) { converted = f; return true; }
                typeError = "must be a number.";
                return false;
            }

            if (fieldType == typeof(Color))
            {
                if (value.IsArray && value.Count == 4)
                {
                    var comps = new float[4];
                    bool allNumeric = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if (!TryAsFloat(value[i], out comps[i])) { allNumeric = false; break; }
                    }
                    if (allNumeric)
                    {
                        converted = new Color(comps[0], comps[1], comps[2], comps[3]);
                        return true;
                    }
                }
                typeError = "must be an array of 4 numbers [r,g,b,a].";
                return false;
            }

            if (fieldType.IsEnum)
            {
                if (value.IsString)
                {
                    string name = (string)value;
                    foreach (string candidate in Enum.GetNames(fieldType))
                    {
                        if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                        {
                            // 原样存字符串, 不在这里转成枚举实例: binding.UseDefaultVisualizer
                            // 的 ConvertTo 本身认得 "target.IsEnum && value is string" 这条路径,
                            // 会按名字自己 Enum.Parse —— 这里只负责确认名字合法, 不用越界去
                            // 构造一个 MCP 侧不能编译期引用的枚举类型的实例。
                            converted = name;
                            return true;
                        }
                    }
                }
                typeError = "must be one of: " + string.Join(", ", Enum.GetNames(fieldType)) + ".";
                return false;
            }

            typeError = "has a value type this build does not know how to validate (" + fieldType.Name + ").";
            return false;
        }

        private static bool TryAsFloat(JsonData v, out float result)
        {
            result = 0f;
            if (v == null) { return false; }
            if (v.IsDouble) { result = (float)(double)v; return true; }
            if (v.IsInt) { result = (int)v; return true; }
            if (v.IsLong) { result = (long)v; return true; }
            return false;
        }

        private static string JoinFieldNames(IDictionary<string, Type> fields)
        {
            var names = new List<string>(fields.Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names.ToArray());
        }

        // 反射解析 FairyGUI.InputVisualStyle 的公开实例字段, 取得"键 -> 声明类型"表。
        // 跟 McpStageInputBinding.TryBind 用一样的手法(按类型全名字符串取, 不编译期引用),
        // 但不复用 binding 内部持有的 Type —— 那是私有字段, 且裁定明确说这层的校验
        // 不进 binding, 各自独立地按同一份真实签名反射, 两边天然不会走出两套键表。
        private static readonly Lazy<IDictionary<string, Type>> StyleFieldTypesLazy =
            new Lazy<IDictionary<string, Type>>(ResolveStyleFieldTypes);

        private static IDictionary<string, Type> StyleFieldTypes => StyleFieldTypesLazy.Value;

        private static IDictionary<string, Type> ResolveStyleFieldTypes()
        {
            Type styleType = typeof(global::FairyGUI.Stage).Assembly.GetType("FairyGUI.InputVisualStyle");
            if (styleType == null) { return null; }

            var fields = new Dictionary<string, Type>();
            foreach (FieldInfo field in styleType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                fields[field.Name] = field.FieldType;
            }
            return fields;
        }
    }
}

using System;
using System.Reflection;
using LitJson;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Scene
{
    // Serializes a Component's Inspector-visible fields ([SerializeField]/public),
    // no property getters (zero side effects). Object references stay shallow.
    public static class ComponentFieldSerializer
    {
        public static JsonData SerializeComponent(Component component, int componentIndex)
        {
            var type = component.GetType();
            var obj = JsonRpcSerializer.Object(
                ("instanceId", component.GetInstanceID()),
                ("typeName", type.FullName),
                ("componentIndex", componentIndex));

            switch (component)
            {
                case Behaviour b:
                    obj["enabled"] = b.enabled;
                    break;
                case Renderer r:
                    obj["enabled"] = r.enabled;
                    break;
                case Collider c:
                    obj["enabled"] = c.enabled;
                    break;
            }

            var fields = new JsonData();
            fields.SetJsonType(JsonType.Array);
            // Walk the type hierarchy from most-derived to base, collecting DeclaredOnly
            // fields at each level. This ensures base-class private [SerializeField] fields
            // are included (type.GetFields without DeclaredOnly misses them). Stop before
            // UnityEngine boundary types to avoid surfacing engine-internal fields.
            var seenFieldNames = new System.Collections.Generic.HashSet<string>();
            for (Type t = type;
                 t != null
                 && t != typeof(UnityEngine.MonoBehaviour)
                 && t != typeof(UnityEngine.Behaviour)
                 && t != typeof(UnityEngine.Component)
                 && t != typeof(UnityEngine.Object)
                 && t != typeof(object);
                 t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    // Keep most-derived declaration when a name is shadowed/redeclared.
                    if (!seenFieldNames.Add(field.Name))
                    {
                        continue;
                    }
                    if (!IsSerialized(field))
                    {
                        continue;
                    }
                    var fieldJson = JsonRpcSerializer.Object(
                        ("name", field.Name), ("typeName", field.FieldType.FullName));
                    fieldJson["value"] = SerializeValue(SafeGet(field, component));
                    fields.Add(fieldJson);
                }
            }
            obj["fields"] = fields;
            return obj;
        }

        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly)
            {
                return false;
            }
            if (field.IsDefined(typeof(NonSerializedAttribute), inherit: true))
            {
                return false;
            }
            return field.IsPublic || field.IsDefined(typeof(SerializeField), inherit: true);
        }

        private static object SafeGet(FieldInfo field, Component component)
        {
            try
            {
                return field.GetValue(component);
            }
            catch
            {
                return null;
            }
        }

        private static JsonData SerializeValue(object value)
        {
            if (value == null)
            {
                return JsonRpcSerializer.Object(("$null", true));
            }

            switch (value)
            {
                case bool b: return Scalar(b);
                case int i: return Scalar(i);
                case byte bt: return Scalar((int)bt);
                case sbyte sb: return Scalar((int)sb);
                case short sh: return Scalar((int)sh);
                case ushort us: return Scalar((int)us);
                case uint ui: return Scalar((long)ui);
                case char ch: return Scalar((int)ch);
                case ulong ul: return Scalar(UlongToScalar(ul));
                case long l: return Scalar(l);
                case float f: return Scalar((double)f);
                case double d: return Scalar(d);
                case string s: return Scalar(s);
                case Enum e: return Scalar(e.ToString());
                case Vector2 v2:
                    return JsonRpcSerializer.Object(("x", (double)v2.x), ("y", (double)v2.y));
                case Vector3 v3:
                    return GameObjectNodeSerializer.Vector3Json(v3);
                case Vector4 v4:
                    return JsonRpcSerializer.Object(
                        ("x", (double)v4.x), ("y", (double)v4.y), ("z", (double)v4.z), ("w", (double)v4.w));
                case Quaternion q:
                    return GameObjectNodeSerializer.QuaternionJson(q);
                case Color col:
                    return JsonRpcSerializer.Object(
                        ("r", (double)col.r), ("g", (double)col.g), ("b", (double)col.b), ("a", (double)col.a));
                case Rect rect:
                    return JsonRpcSerializer.Object(
                        ("x", (double)rect.x), ("y", (double)rect.y),
                        ("width", (double)rect.width), ("height", (double)rect.height));
                case Bounds bounds:
                    return JsonRpcSerializer.Object(
                        ("center", GameObjectNodeSerializer.Vector3Json(bounds.center)),
                        ("size", GameObjectNodeSerializer.Vector3Json(bounds.size)));
            }

            if (value is UnityEngine.Object unityObj)
            {
                // A destroyed Object compares == null; treat as null placeholder.
                if (unityObj == null)
                {
                    return JsonRpcSerializer.Object(("$null", true));
                }
                return JsonRpcSerializer.Object(
                    ("instanceId", unityObj.GetInstanceID()), ("typeName", unityObj.GetType().FullName));
            }

            return Scalar(value.ToString());
        }

        // ulong 在 long 范围内转 long, 否则转字符串 (LitJson long 无法承载超范围值)。
        private static object UlongToScalar(ulong value)
            => value <= long.MaxValue ? (object)(long)value : value.ToString();

        private static JsonData Scalar(object value)
        {
            var data = new JsonData();
            switch (value)
            {
                case bool b: data = (JsonData)b; break;
                case int i: data = (JsonData)i; break;
                case long l: data = (JsonData)l; break;
                case double d: data = (JsonData)d; break;
                case string s: data = (JsonData)s; break;
            }
            return data;
        }
    }
}

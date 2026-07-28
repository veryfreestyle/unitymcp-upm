using LitJson;
using UnityEditor;
using UnityEngine;
using VeryFS.UnityMCP.Editor.Protocol;

namespace VeryFS.UnityMCP.Editor.Commands.Asset
{
    // get-info 的 details: 按主对象类型特化。加新类型只加一个 case 分支, 不加 action。
    public static class AssetDetailsSerializer
    {
        // Unity 在 shader 解析不开时挂上的内置错误 shader。
        private const string InternalErrorShaderName = "Hidden/InternalErrorShader";

        public static JsonData Serialize(UnityEngine.Object mainObject)
        {
            switch (mainObject)
            {
                case Material material:
                    return SerializeMaterial(material);
                case GameObject go:
                    return SerializeGameObject(go);
                default:
                    return JsonRpcSerializer.Object();
            }
        }

        private static JsonData SerializeMaterial(Material material)
        {
            var so = new SerializedObject(material);
            SerializedProperty shaderProp = so.FindProperty("m_Shader");
            bool resolved = shaderProp != null && shaderProp.objectReferenceValue != null;
            Shader shader = material.shader;

            var details = JsonRpcSerializer.Object(
                ("shaderName", resolved && shader != null ? shader.name : null),
                ("shaderResolved", resolved),
                ("isFallbackShader", IsFallbackShader(shader, resolved)),
                ("renderQueue", material.renderQueue));
            details["enabledKeywords"] = StringArray(material.shaderKeywords);
            details["textureSlots"] = SerializeTextureSlots(so);
            return details;
        }

        // 判据取自 Task 1 探针在真实 Editor 上的实测结果 (见 plan 的「探针结论」)。
        // 结论里未触发的信号必须从这里删掉, 不留凭推断写下的条款。
        private static bool IsFallbackShader(Shader shader, bool resolved)
        {
            if (!resolved || shader == null)
            {
                return true;
            }
            return shader.name == InternalErrorShaderName || !shader.isSupported;
        }

        // 直接读材质自己序列化的 m_TexEnvs, 不走 Material.GetTexturePropertyNames ——
        // 后者问的是当前 shader 有哪些属性, shader 解析不开时会把材质存过的槽全丢掉。
        private static JsonData SerializeTextureSlots(SerializedObject so)
        {
            var slots = new JsonData();
            slots.SetJsonType(JsonType.Array);

            SerializedProperty texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null || !texEnvs.isArray)
            {
                return slots;
            }

            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                SerializedProperty entry = texEnvs.GetArrayElementAtIndex(i);
                SerializedProperty nameProp = entry.FindPropertyRelative("first");
                SerializedProperty textureProp = entry.FindPropertyRelative("second.m_Texture");
                if (nameProp == null || textureProp == null)
                {
                    continue;
                }

                string state;
                string path = null;
                if (textureProp.objectReferenceValue != null)
                {
                    state = "resolved";
                    path = AssetDatabase.GetAssetPath(textureProp.objectReferenceValue);
                }
                else if (textureProp.objectReferenceInstanceIDValue != 0)
                {
                    // 存了引用但解析不开: Unity 给一个悬空 instance id, 对象取不到。
                    state = "broken";
                }
                else
                {
                    state = "unassigned";
                }

                slots.Add(JsonRpcSerializer.Object(
                    ("name", nameProp.stringValue), ("state", state), ("path", path)));
            }
            return slots;
        }

        private static JsonData SerializeGameObject(GameObject go)
            => JsonRpcSerializer.Object(
                ("prefabAssetType", PrefabUtility.GetPrefabAssetType(go).ToString()));

        private static JsonData StringArray(string[] values)
        {
            var data = new JsonData();
            data.SetJsonType(JsonType.Array);
            if (values != null)
            {
                foreach (string value in values) { data.Add(value); }
            }
            return data;
        }
    }
}

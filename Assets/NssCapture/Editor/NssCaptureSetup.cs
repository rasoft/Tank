using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// One-time project setup: NssMvTriggerFeature installed on every URP renderer data
// plus the default config asset. The project color space is deliberately NOT switched:
// the pipeline records the actual color space in metadata/dataset_spec (manual 16),
// and switching alone invalidates baked lighting on Gamma-authored scenes.
public static class NssCaptureSetup
{
    public const string ConfigPath = "Assets/NssCapture/NssCaptureConfig.asset";

    public static NssCaptureConfig EnsureConfig()
    {
        var cfg = AssetDatabase.LoadAssetAtPath<NssCaptureConfig>(ConfigPath);
        if (cfg == null)
        {
            cfg = ScriptableObject.CreateInstance<NssCaptureConfig>();
            AssetDatabase.CreateAsset(cfg, ConfigPath);
            AssetDatabase.SaveAssets();
        }
        return cfg;
    }

    public static List<string> EnsureSetup()
    {
        var log = new List<string>();

        EnsureConfig();

        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableRendererData"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.StartsWith("Packages/")) continue; // immutable package assets: never modify
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (data == null) continue;

            bool present = false;
            foreach (var f in data.rendererFeatures)
                if (f is NssMvTriggerFeature) { present = true; break; }
            if (present) { log.Add($"{Path.GetFileName(path)}: feature already installed."); continue; }

            var feat = ScriptableObject.CreateInstance<NssMvTriggerFeature>();
            feat.name = "NssMvTriggerFeature";
            AssetDatabase.AddObjectToAsset(feat, data);

            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feat;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            // Keep the renderer feature map in sync (local file id list).
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feat, out _, out long localId))
            {
                var so2 = new SerializedObject(data);
                var map = so2.FindProperty("m_RendererFeatureMap");
                map.InsertArrayElementAtIndex(map.arraySize);
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so2.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(data);
            }
            log.Add($"{Path.GetFileName(path)}: NssMvTriggerFeature installed.");
        }

        AssetDatabase.SaveAssets();
        return log;
    }

    public static string GetOutputRoot()
    {
        var cfg = EnsureConfig();
        return Path.GetFullPath(Path.Combine(Application.dataPath, cfg.outputRoot));
    }
}

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Glassmorphism.Editor
{
    [CustomEditor(typeof(UIBlurRendererFeature))]
    public sealed class UIBlurRendererFeatureEditor : UnityEditor.Editor
    {
        const int ForegroundLayer = 31;
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GetAssetPath(target));
            if (renderer == null) return;
            string layer = LayerMask.LayerToName(ForegroundLayer);
            bool conflict = !string.IsNullOrEmpty(layer) && layer != "UIBlurForeground";
            bool excluded = (renderer.transparentLayerMask.value & (1 << ForegroundLayer)) == 0;
            if (conflict)
            {
                EditorGUILayout.HelpBox("Screen Capture reserves layer 31 for UIBlurForeground. Move the existing layer to a free slot before using this source. Shared Backdrop does not require it.", MessageType.Error);
                return;
            }
            if (excluded && layer == "UIBlurForeground") return;
            EditorGUILayout.HelpBox("Screen Capture needs a foreground UI layer excluded from the normal transparent pass. The feature draws that layer after capturing the background. The viewing camera must still include layer 31 in its Culling Mask.", MessageType.Info);
            if (!GUILayout.Button("Configure Screen Capture UI Layer")) return;
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            tags.FindProperty("layers").GetArrayElementAtIndex(ForegroundLayer).stringValue = "UIBlurForeground";
            tags.ApplyModifiedProperties();
            Undo.RecordObject(renderer, "Configure UI Blur foreground");
            renderer.transparentLayerMask = renderer.transparentLayerMask.value & ~(1 << ForegroundLayer);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
        }
    }
}

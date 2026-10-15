using UnityEditor;
using UnityEngine;

namespace Glassmorphism.Editor
{
    [CustomEditor(typeof(UIBlur)), CanEditMultipleObjects]
    public sealed class UIBlurEditor : UnityEditor.Editor
    {
        void Field(string name) => EditorGUILayout.PropertyField(serializedObject.FindProperty(name), true);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            Field("source");
            Field("sourceCamera");
            var source = serializedObject.FindProperty("source");
            if (!source.hasMultipleDifferentValues)
            {
                if (source.enumValueIndex == (int)UIBlurSource.ScreenCapture)
                {
                    EditorGUILayout.HelpBox("Screen Capture module: the main camera renders background UI, shares blur results for matching settings, then draws panels and child UI. Uses a Screen Space Camera source canvas. For VR, choose Shared Backdrop; an active stereo camera automatically switches to that module.", MessageType.Info);
                    Field("sourceCanvas");
                    EditorGUILayout.HelpBox("Requires UI Blur Renderer Feature and the reserved UIBlurForeground layer excluded from the renderer’s normal transparent pass.", MessageType.Info);
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Capture Quality", EditorStyles.boldLabel);
                    Field("downsample");
                    Field("blurSize");
                    Field("blurIterations");
                    Field("hdrCapture");
                }
                else
                {
                    EditorGUILayout.HelpBox("Shared Backdrop module: World Space and Screen Space Camera canvases use UI Blur Renderer Feature. Its quality settings are shared by all panels. Supports XR stereo on World Space canvases, with a separate backdrop slice for each eye. Also includes camera backgrounds supplied by AR Foundation or the phone camera test adapter.", MessageType.Info);
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Panel Appearance", EditorStyles.boldLabel);
            Field("panelImage");
            Field("imageType");
            if (serializedObject.FindProperty("imageType").enumValueIndex == (int)UIBlurImageType.Sliced)
                Field("pixelsPerUnitMultiplier");
            Field("imageColor");
            if (serializedObject.FindProperty("imageType").enumValueIndex == (int)UIBlurImageType.Simple)
                Field("preserveImageAspect");
            Field("maskWithPanelImage");
            Field("tint");
            Field("cornerRadius");
            Field("borderWidth");
            Field("borderColor");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shaders", EditorStyles.boldLabel);
            if (source.hasMultipleDifferentValues || source.enumValueIndex == (int)UIBlurSource.ScreenCapture)
            {
                Field("capturePanelShader");
            }
            if (source.hasMultipleDifferentValues || source.enumValueIndex == (int)UIBlurSource.SharedBackdrop)
                Field("backdropPanelShader");
            if (serializedObject.ApplyModifiedProperties())
                foreach (UIBlur panel in targets)
                    panel.Synchronize();
        }
    }
}

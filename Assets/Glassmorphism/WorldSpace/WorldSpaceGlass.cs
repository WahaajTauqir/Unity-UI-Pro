using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Glassmorphism
{
    /// World space and Screen Space - Camera uGUI glass, including handheld AR.
    /// WorldGlassRendererFeature supplies one shared backdrop per viewing camera.
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage), typeof(Mask))]
    [AddComponentMenu("Glassmorphism/World Space Glass")]
    public sealed class WorldSpaceGlass : MonoBehaviour
    {
        [Tooltip("The camera viewing this canvas. Assign the AR Camera in AR. Falls back to the canvas camera, then Camera.main.")]
        public Camera sourceCamera;
        [Tooltip("Optional live phone camera backdrop. Assign this for Screen Space Overlay camera-preview UI.")]
        public PhoneCameraBackdrop cameraBackdrop;

        [Header("Panel Appearance")]
        public Sprite panelImage;
        public GlassImageType imageType = GlassImageType.Simple;
        [Tooltip("Sliced images: higher values make the sprite borders thinner. Requires borders in Sprite Editor.")]
        [Min(0.01f)] public float pixelsPerUnitMultiplier = 1f;
        public Color imageColor = Color.white;
        [Tooltip("Fit the sprite in Simple mode. Sliced sprites fill the panel rect.")]
        public bool preserveImageAspect = true;
        [Tooltip("Clip the glass and child UI to the sprite silhouette, independently of its decorative opacity.")]
        public bool maskWithPanelImage = true;
        public Color tint = Color.clear;
        [Tooltip("Radius in local canvas units.")]
        [Min(0)] public float cornerRadius;
        [Tooltip("Width in local canvas units. Zero uses only the image's original border.")]
        [Min(0)] public float borderWidth;
        public Color borderColor = new Color(1, 1, 1, 0.5f);
        [Tooltip("Keep this reference assigned so the world space shader is included in builds.")]
        public Shader panelShader;

        static readonly HashSet<WorldSpaceGlass> Panels = new HashSet<WorldSpaceGlass>();
        static readonly int PanelRect = Shader.PropertyToID("_PanelRect");
        static readonly int PanelCamera = Shader.PropertyToID("_PanelCameraID");
        static readonly int OverlayTex = Shader.PropertyToID("_OverlayTex");
        static readonly int OverlayUV = Shader.PropertyToID("_OverlayUV");
        static readonly int OverlayRect = Shader.PropertyToID("_OverlayRect");
        static readonly int OverlayColor = Shader.PropertyToID("_OverlayColor");
        static readonly int OverlayEnabled = Shader.PropertyToID("_OverlayEnabled");
        static readonly int MaskWithImage = Shader.PropertyToID("_MaskWithImage");
        static readonly int TintColor = Shader.PropertyToID("_TintColor");
        static readonly int CornerRadius = Shader.PropertyToID("_CornerRadius");
        static readonly int BorderWidth = Shader.PropertyToID("_BorderWidth");
        static readonly int BorderColor = Shader.PropertyToID("_BorderColor");
        static readonly int DirectBackdrop = Shader.PropertyToID("_DirectBackdrop");
        static readonly int UseDirectBackdrop = Shader.PropertyToID("_UseDirectBackdrop");
        static readonly int DirectBackdropReady = Shader.PropertyToID("_DirectBackdropReady");

        RawImage image;
        RectTransform rectTransform;
        Canvas canvas;
        Material material;
        Material previousMaterial;
        Texture previousTexture;
        Rect previousUV;

        internal Camera ViewingCamera => sourceCamera != null ? sourceCamera
            : canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;

        internal static bool HasPanelsFor(Camera camera)
        {
            foreach (WorldSpaceGlass panel in Panels)
            {
                if (panel != null && panel.cameraBackdrop == null && panel.isActiveAndEnabled && panel.canvas != null
                    && panel.canvas.isActiveAndEnabled && panel.canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    && panel.ViewingCamera == camera)
                    return true;
            }
            return false;
        }

        void Reset()
        {
            panelShader = Shader.Find("Glassmorphism/WorldSpaceGlass");
            GetComponent<RawImage>().raycastTarget = false;
        }

        void OnValidate()
        {
            if (panelShader == null) panelShader = Shader.Find("Glassmorphism/WorldSpaceGlass");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnEnable()
        {
            image = GetComponent<RawImage>();
            rectTransform = (RectTransform)transform;
            canvas = GetComponentInParent<Canvas>();
            previousMaterial = image.material;
            previousTexture = image.texture;
            previousUV = image.uvRect;
            GetComponent<Mask>().showMaskGraphic = true;
            Panels.Add(this);
            Canvas.willRenderCanvases += ApplyAppearance;
            ApplyAppearance();
        }

        void OnTransformParentChanged() => canvas = GetComponentInParent<Canvas>();

        void OnDisable()
        {
            Panels.Remove(this);
            Canvas.willRenderCanvases -= ApplyAppearance;
            if (image != null && image.material == material)
            {
                image.material = previousMaterial;
                image.texture = previousTexture;
                image.uvRect = previousUV;
            }
            DestroyMaterial();
        }

        void DestroyMaterial()
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
            material = null;
        }

        void ApplyAppearance()
        {
            if (image == null || rectTransform == null) return;
            Shader shader = panelShader != null ? panelShader : Shader.Find("Glassmorphism/WorldSpaceGlass");
            if (shader == null) return;
            if (material != null && material.shader != shader) DestroyMaterial();
            if (material == null)
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (image.material != material) image.material = material;
            // UV0 belongs to the decorative image. Backdrop coordinates are projected
            // per fragment in the shader, so rotation and perspective stay aligned.
            if (image.texture != Texture2D.whiteTexture) image.texture = Texture2D.whiteTexture;
            if (image.uvRect != new Rect(0, 0, 1, 1)) image.uvRect = new Rect(0, 0, 1, 1);

            Rect rect = rectTransform.rect;
            Vector4 imageRect = new Vector4(0, 0, 1, 1);
            if (panelImage != null && !GlassImageSlicing.IsSliced(panelImage, imageType)
                && preserveImageAspect && rect.width > 0 && rect.height > 0)
            {
                float imageAspect = panelImage.rect.width / Mathf.Max(1, panelImage.rect.height);
                float panelAspect = rect.width / rect.height;
                if (imageAspect > panelAspect)
                {
                    imageRect.w = panelAspect / imageAspect;
                    imageRect.y = (1 - imageRect.w) * 0.5f;
                }
                else
                {
                    imageRect.z = imageAspect / panelAspect;
                    imageRect.x = (1 - imageRect.z) * 0.5f;
                }
            }
            Vector4 uv = panelImage != null ? UnityEngine.Sprites.DataUtility.GetOuterUV(panelImage)
                : new Vector4(0, 0, 1, 1);
            Camera camera = ViewingCamera;
            float cameraId = camera != null ? camera.GetInstanceID() : 0;
            ApplyUniforms(material, rect, imageRect, uv, cameraId);
            Material stencilMaterial = image.materialForRendering;
            if (stencilMaterial != material) ApplyUniforms(stencilMaterial, rect, imageRect, uv, cameraId);
            if (image.canvasRenderer.popMaterialCount > 0)
            {
                Material pop = image.canvasRenderer.GetPopMaterial(0);
                if (pop != null) ApplyUniforms(pop, rect, imageRect, uv, cameraId);
            }
        }

        void ApplyUniforms(Material target, Rect rect, Vector4 imageRect, Vector4 uv, float cameraId)
        {
            target.SetFloat(UseDirectBackdrop, cameraBackdrop != null ? 1 : 0);
            target.SetFloat(DirectBackdropReady, cameraBackdrop != null && cameraBackdrop.IsReady ? 1 : 0);
            target.SetTexture(DirectBackdrop, cameraBackdrop != null && cameraBackdrop.IsReady
                ? cameraBackdrop.BlurredTexture : Texture2D.blackTexture);
            GlassImageSlicing.Apply(target, panelImage, imageType, rect, canvas, pixelsPerUnitMultiplier);
            target.SetFloat(PanelCamera, cameraId);
            target.SetVector(PanelRect, new Vector4(rect.xMin, rect.yMin,
                Mathf.Max(0.0001f, rect.width), Mathf.Max(0.0001f, rect.height)));
            target.SetTexture(OverlayTex, panelImage != null ? panelImage.texture : Texture2D.whiteTexture);
            target.SetVector(OverlayUV, uv);
            target.SetVector(OverlayRect, imageRect);
            target.SetColor(OverlayColor, imageColor);
            target.SetFloat(OverlayEnabled, panelImage != null ? 1 : 0);
            target.SetFloat(MaskWithImage, maskWithPanelImage ? 1 : 0);
            target.SetColor(TintColor, tint);
            target.SetFloat(CornerRadius, cornerRadius);
            target.SetFloat(BorderWidth, borderWidth);
            target.SetColor(BorderColor, borderColor);
        }
    }
}

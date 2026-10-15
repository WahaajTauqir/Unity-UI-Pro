using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

namespace Glassmorphism
{
    /// Frosted-glass backdrop blur for uGUI.
    /// Main-camera UI capture: the renderer feature draws background UI, shares
    /// matching blur settings, then draws these panels/children using native UI.
    /// No secondary camera, foreground culling toggle or scene rerender.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage))]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(Mask))]
    [AddComponentMenu("")]
    // INTERNAL MODULE 1: main-camera screen/UI capture. UIBlur is the user-facing component.
    // Script GUID and MovedFrom metadata retain existing scene/serialized references.
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Glassmorphism", "Glassmorphism", "UIBlurScreenCapture")]
    public sealed class UIBlurScreenCapture : MonoBehaviour
    {
        [Tooltip("Gameplay / UI camera. Leave empty to use Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("Canvas used for camera/layout context. Background UI rendered by the viewing camera is included. Leave empty to auto-find.")]
        public Canvas sourceCanvas;

        [Tooltip("Blur at 1/N of screen resolution while capturing UI at native size to preserve its layout.")]
        [Range(1, 8)] public int downsample = 2;
        [Tooltip("Blur radius in downsampled texture pixels.")]
        [Range(0f, 8f)] public float blurSize = 1f;
        [Tooltip("Number of horizontal and vertical blur pairs. One is usually enough.")]
        [Range(1, 6)] public int blurIterations = 1;
        [Tooltip("Use a higher bandwidth HDR capture only when the glass must preserve values above 1.")]
        public bool hdrCapture;

        [Header("Panel Appearance")]
        [Tooltip("One sprite supplies the glass shape and decorative overlay. Its alpha also clips child UI.")]
        public Sprite panelImage;
        public UIBlurImageType imageType = UIBlurImageType.Simple;
        [Tooltip("Sliced images: higher values make the sprite borders thinner. Requires borders in Sprite Editor.")]
        [Min(0.01f)] public float pixelsPerUnitMultiplier = 1f;
        public Color imageColor = Color.white;
        [Tooltip("Fit the sprite in Simple mode. Sliced sprites fill the panel rect.")]
        public bool preserveImageAspect = true;
        [Tooltip("Use the assigned sprite's alpha as the panel and child-content mask. Decoration opacity is independent.")]
        public bool maskWithPanelImage = true;
        [Tooltip("Alpha controls how strongly the tint covers the backdrop.")]
        public Color tint = Color.clear;
        [Min(0f)] public float cornerRadius;
        [Min(0f)] public float borderWidth;
        public Color borderColor = new Color(1f, 1f, 1f, 0.5f);

        [Header("Shaders")]
        [HideInInspector, Tooltip("Legacy field. Blur shader now belongs to UIBlurRendererFeature.")]
        public Shader blurShader;
        [Tooltip("UI shader that combines the backdrop, panel image, tint and border.")]
        public Shader panelShader;

        RawImage rawImage;
        RectTransform rectTransform;
        Material panelMaterial, previousMaterial;
        Texture previousTexture;
        Rect previousUV;
        int captureGroup = MaxCaptureGroups;
        float captureCameraId;
        bool reportedInvalid;
        internal const int ForegroundLayer = 31;
        internal const int MaxCaptureGroups = 8;
        internal static readonly HashSet<UIBlurScreenCapture> Panels = new HashSet<UIBlurScreenCapture>();
        [System.Serializable]
        struct CanvasLayerState { public Canvas canvas; public int layer; }
        [SerializeField, HideInInspector] List<CanvasLayerState> stagedCanvases = new List<CanvasLayerState>();
        readonly List<Canvas> childCanvases = new List<Canvas>();
        [SerializeField, HideInInspector] Canvas stageCanvas;
        [SerializeField, HideInInspector] GraphicRaycaster stageRaycaster;
        static readonly int IdCaptureGroup = Shader.PropertyToID("_CaptureGroup");
        static readonly int IdCaptureCamera = Shader.PropertyToID("_CaptureCameraID");
        static readonly int IdPanelRect = Shader.PropertyToID("_PanelRect");
        static readonly int IdBackdropUV = Shader.PropertyToID("_BackdropUV");
        static readonly int IdOverlayTex = Shader.PropertyToID("_OverlayTex");
        static readonly int IdOverlayUV = Shader.PropertyToID("_OverlayUV");
        static readonly int IdOverlayRect = Shader.PropertyToID("_OverlayRect");
        static readonly int IdOverlayColor = Shader.PropertyToID("_OverlayColor");
        static readonly int IdOverlayEnabled = Shader.PropertyToID("_OverlayEnabled");
        static readonly int IdMaskWithImage = Shader.PropertyToID("_MaskWithImage");
        static readonly int IdTint = Shader.PropertyToID("_TintColor");
        static readonly int IdCornerRadius = Shader.PropertyToID("_CornerRadius");
        static readonly int IdBorderWidth = Shader.PropertyToID("_BorderWidth");
        static readonly int IdBorderColor = Shader.PropertyToID("_BorderColor");
        static readonly Vector3[] Corners = new Vector3[4];
        bool appearanceValid;
        AppearanceState appearance;
        Vector4 cachedImageRect, cachedImageUV, lastBackdropUV;
        Material lastBaseMaterial, lastRenderingMaterial, lastPopMaterial;

        struct AppearanceState
        {
            public Sprite sprite;
            public Texture texture;
            public UIBlurImageType imageType;
            public Rect rect;
            public float referencePPU, multiplier, corner, borderWidth;
            public Color imageColor, tint, borderColor;
            public bool preserveAspect, mask;
            public bool Matches(AppearanceState other) =>
                sprite == other.sprite && texture == other.texture && imageType == other.imageType
                && rect.Equals(other.rect) && referencePPU.Equals(other.referencePPU)
                && multiplier.Equals(other.multiplier) && corner.Equals(other.corner)
                && borderWidth.Equals(other.borderWidth) && imageColor.Equals(other.imageColor)
                && tint.Equals(other.tint) && borderColor.Equals(other.borderColor)
                && preserveAspect == other.preserveAspect && mask == other.mask;
        }

        void InvalidateAppearance() => appearanceValid = false;

        void OnValidate()
        {
            InvalidateAppearance();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnEnable()
        {
            InvalidateAppearance();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged += InvalidateAppearance;
#endif
            rawImage = GetComponent<RawImage>();
            previousMaterial = rawImage.material;
            previousTexture = rawImage.texture;
            previousUV = rawImage.uvRect;
            GetComponent<Mask>().showMaskGraphic = true;
            rectTransform = (RectTransform)transform;
            ResolveSourceCanvas();
            EnsureStageCanvas();
            if (Panels.Count == 0) RenderPipelineManager.beginCameraRendering += PrepareCamera;
            Panels.Add(this);
            RefreshPanel();
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged -= InvalidateAppearance;
#endif
            Panels.Remove(this);
            if (Panels.Count == 0) RenderPipelineManager.beginCameraRendering -= PrepareCamera;
            foreach (var state in stagedCanvases)
                if (state.canvas != null) state.canvas.gameObject.layer = state.layer;
            stagedCanvases.Clear();
            if (rawImage != null && rawImage.material == panelMaterial)
            {
                rawImage.material = previousMaterial;
                rawImage.texture = previousTexture;
                rawImage.uvRect = previousUV;
            }
            DestroySafe(ref panelMaterial);
            InvalidateAppearance();
            captureGroup = MaxCaptureGroups;
        }

        static void PrepareCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType == CameraType.Game && !camera.stereoEnabled)
                UIBlurScreenCapturePass.PreparePanels(camera);
        }

        void LateUpdate()
        {
            Canvas.ForceUpdateCanvases();
            RefreshPanel();
        }

        internal Camera ViewingCamera => sourceCamera != null ? sourceCamera
            : sourceCanvas != null && sourceCanvas.worldCamera != null ? sourceCanvas.worldCamera
            : rawImage != null && rawImage.canvas != null && rawImage.canvas.worldCamera != null ? rawImage.canvas.worldCamera
            : Camera.main;

        internal bool CanCapture(Camera camera)
        {
            return isActiveAndEnabled && gameObject.activeInHierarchy && ViewingCamera == camera
                && rawImage != null && rawImage.canvas != null
                && rawImage.canvas.renderMode == RenderMode.ScreenSpaceCamera && !camera.stereoEnabled && !HasBackgroundStencilMask();
        }

        bool HasBackgroundStencilMask()
        {
            for (var parent = transform.parent; parent != null; parent = parent.parent)
            {
                var capture = parent.GetComponent<UIBlurScreenCapture>();
                if (capture != null && capture.isActiveAndEnabled) return false;
                var mask = parent.GetComponent<Mask>();
                if (mask != null && mask.IsActive()) return true;
            }
            return false;
        }

        void EnsureStageCanvas()
        {
            if (stageCanvas == null)
            {
                stageCanvas = GetComponent<Canvas>();
                if (stageCanvas == null)
                {
                    stageCanvas = gameObject.AddComponent<Canvas>();
                    stageCanvas.overrideSorting = false;
                    stageCanvas.hideFlags |= HideFlags.HideInInspector;
                }
            }
            if (sourceCanvas != null && stageCanvas != sourceCanvas)
                stageCanvas.additionalShaderChannels |= sourceCanvas.additionalShaderChannels;
            if (stageRaycaster == null)
            {
                stageRaycaster = GetComponent<GraphicRaycaster>();
                if (stageRaycaster == null)
                {
                    var original = sourceCanvas != null ? sourceCanvas.GetComponent<GraphicRaycaster>() : null;
                    stageRaycaster = gameObject.AddComponent<GraphicRaycaster>();
                    stageRaycaster.hideFlags |= HideFlags.HideInInspector;
                    if (original != null)
                    {
                        stageRaycaster.blockingObjects = original.blockingObjects;
                        stageRaycaster.blockingMask = original.blockingMask;
                        stageRaycaster.ignoreReversedGraphics = original.ignoreReversedGraphics;
                    }
                }
            }
        }

        void RefreshPanel()
        {
            if (rawImage == null || rectTransform == null) return;
            ResolveSourceCanvas();
            EnsureStageCanvas();
            Camera camera = ViewingCamera;
            if (camera == null) return;
            if (rawImage.canvas == null || rawImage.canvas.renderMode != RenderMode.ScreenSpaceCamera || HasBackgroundStencilMask())
            {
                if (!reportedInvalid)
                    Debug.LogWarning("[UIBlur] Screen Capture requires a Screen Space Camera canvas. Keep stencil Masks inside the foreground panel; ancestor RectMask2D clipping is supported.", this);
                reportedInvalid = true;
                return;
            }
            reportedInvalid = false;
            // Canvas boundaries let URP filter native UI batches without moving UI
            // objects or rewriting their materials. Graphic children keep their layers.
            GetComponentsInChildren(true, childCanvases);
            for (int i = stagedCanvases.Count - 1; i >= 0; i--)
            {
                var state = stagedCanvases[i];
                if (state.canvas == null || !state.canvas.transform.IsChildOf(transform))
                {
                    if (state.canvas != null) state.canvas.gameObject.layer = state.layer;
                    stagedCanvases.RemoveAt(i);
                }
            }
            foreach (var child in childCanvases)
            {
                bool found = false;
                foreach (var state in stagedCanvases) if (state.canvas == child) { found = true; break; }
                if (!found) stagedCanvases.Add(new CanvasLayerState { canvas = child, layer = child.gameObject.layer });
                if (child.gameObject.layer != ForegroundLayer) child.gameObject.layer = ForegroundLayer;
            }
            if (TryGetScreenRect(camera, out Rect rect))
            {
                Rect viewport = camera.pixelRect;
                var uv = new Rect((rect.xMin - viewport.xMin) / Mathf.Max(1, camera.pixelWidth),
                    (rect.yMin - viewport.yMin) / Mathf.Max(1, camera.pixelHeight),
                    rect.width / Mathf.Max(1, camera.pixelWidth), rect.height / Mathf.Max(1, camera.pixelHeight));
                if (rawImage.uvRect != uv) rawImage.uvRect = uv;
            }
            if (rawImage.texture != Texture2D.whiteTexture) rawImage.texture = Texture2D.whiteTexture;
            ApplyPanelAppearance();
        }

        internal void BindCaptureGroup(Camera camera, int group)
        {
            bool changed = captureGroup != group || captureCameraId != camera.GetInstanceID();
            captureGroup = group;
            captureCameraId = camera.GetInstanceID();
            if (changed) InvalidateAppearance();
            ApplyPanelAppearance();
        }

        void ResolveSourceCanvas()
        {
            if (sourceCanvas != null) return;
            var own = GetComponentInParent<Canvas>();
            if (own != null) sourceCanvas = own.rootCanvas != null ? own.rootCanvas : own;
        }

        bool TryGetScreenRect(Camera cam, out Rect rect)
        {
            rectTransform.GetWorldCorners(Corners);

            Canvas ownCanvas = GetComponentInParent<Canvas>();
            Camera eventCam = null;
            if (ownCanvas != null && ownCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                eventCam = ownCanvas.worldCamera != null ? ownCanvas.worldCamera : cam;

            Vector2 s0 = RectTransformUtility.WorldToScreenPoint(eventCam, Corners[0]);
            Vector2 s1 = RectTransformUtility.WorldToScreenPoint(eventCam, Corners[1]);
            Vector2 s2 = RectTransformUtility.WorldToScreenPoint(eventCam, Corners[2]);
            Vector2 s3 = RectTransformUtility.WorldToScreenPoint(eventCam, Corners[3]);

            float xMin = Mathf.Min(Mathf.Min(s0.x, s1.x), Mathf.Min(s2.x, s3.x));
            float xMax = Mathf.Max(Mathf.Max(s0.x, s1.x), Mathf.Max(s2.x, s3.x));
            float yMin = Mathf.Min(Mathf.Min(s0.y, s1.y), Mathf.Min(s2.y, s3.y));
            float yMax = Mathf.Max(Mathf.Max(s0.y, s1.y), Mathf.Max(s2.y, s3.y));

            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return rect.width >= 2f && rect.height >= 2f && rect.Overlaps(cam.pixelRect);
        }

        void ApplyPanelAppearance()
        {
            if (rawImage == null || rectTransform == null) return;
            Shader shader = panelShader != null ? panelShader : Shader.Find("Glassmorphism/GlassUIPanel");
            if (shader == null) return;
            if (panelMaterial != null && panelMaterial.shader != shader)
            {
                rawImage.material = previousMaterial;
                DestroySafe(ref panelMaterial);
            }
            if (panelMaterial == null)
                panelMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (rawImage.material != panelMaterial)
                rawImage.material = panelMaterial;

            Rect rect = rectTransform.rect;
            var canvas = rawImage.canvas;
            var state = new AppearanceState
            {
                sprite = panelImage,
                texture = panelImage != null ? panelImage.texture : null,
                imageType = imageType, rect = rect,
                referencePPU = canvas != null ? canvas.referencePixelsPerUnit : 100,
                multiplier = pixelsPerUnitMultiplier, corner = cornerRadius, borderWidth = borderWidth,
                imageColor = imageColor, tint = tint, borderColor = borderColor,
                preserveAspect = preserveImageAspect, mask = maskWithPanelImage
            };
            bool changed = !appearanceValid || !appearance.Matches(state);
            bool baseChanged = lastBaseMaterial != panelMaterial;
            if (changed || baseChanged)
            {
                Vector4 imageRect = new Vector4(0f, 0f, 1f, 1f);
                if (panelImage != null && !UIBlurImageSlicing.IsSliced(panelImage, imageType)
                    && preserveImageAspect && rect.width > 0f && rect.height > 0f)
                {
                    float imageAspect = panelImage.rect.width / Mathf.Max(1f, panelImage.rect.height);
                    float panelAspect = rect.width / rect.height;
                    if (imageAspect > panelAspect)
                    {
                        imageRect.w = panelAspect / imageAspect;
                        imageRect.y = (1f - imageRect.w) * 0.5f;
                    }
                    else
                    {
                        imageRect.z = imageAspect / panelAspect;
                        imageRect.x = (1f - imageRect.z) * 0.5f;
                    }
                }
                Vector4 imageUV = panelImage != null
                    ? UnityEngine.Sprites.DataUtility.GetOuterUV(panelImage) : new Vector4(0f, 0f, 1f, 1f);
                cachedImageRect = imageRect;
                cachedImageUV = imageUV;
            }

            Rect uv = rawImage.uvRect;
            var backdropUV = new Vector4(uv.x, uv.y, Mathf.Max(0.0001f, uv.width), Mathf.Max(0.0001f, uv.height));
            bool uvChanged = !appearanceValid || !lastBackdropUV.Equals(backdropUV);
            ApplyMaterialChanges(panelMaterial, changed || baseChanged, uvChanged || baseChanged, rect, backdropUV);

            // Mask and pop copies can be replaced independently of appearance.
            Material renderingMaterial = rawImage.materialForRendering;
            if (renderingMaterial != null && renderingMaterial != panelMaterial)
            {
                bool newMaterial = renderingMaterial != lastRenderingMaterial;
                ApplyMaterialChanges(renderingMaterial, changed || newMaterial, uvChanged || newMaterial, rect, backdropUV);
            }
            Material pop = rawImage.canvasRenderer.popMaterialCount > 0 ? rawImage.canvasRenderer.GetPopMaterial(0) : null;
            if (pop != null && pop != panelMaterial && pop != renderingMaterial)
            {
                bool newMaterial = pop != lastPopMaterial;
                ApplyMaterialChanges(pop, changed || newMaterial, uvChanged || newMaterial, rect, backdropUV);
            }
            appearance = state;
            lastBackdropUV = backdropUV;
            appearanceValid = true;
            lastBaseMaterial = panelMaterial;
            lastRenderingMaterial = renderingMaterial;
            lastPopMaterial = pop;
        }

        void ApplyMaterialChanges(Material target, bool appearanceChanged, bool uvChanged, Rect rect, Vector4 backdropUV)
        {
            if (appearanceChanged) ApplyPanelUniforms(target, rect, cachedImageRect, cachedImageUV);
            if (uvChanged) target.SetVector(IdBackdropUV, backdropUV);
        }

        void ApplyPanelUniforms(Material material, Rect rect, Vector4 imageRect, Vector4 imageUV)
        {
            material.SetFloat(IdCaptureGroup, captureGroup);
            material.SetFloat(IdCaptureCamera, captureCameraId);
            UIBlurImageSlicing.Apply(material, panelImage, imageType, rect, rawImage.canvas, pixelsPerUnitMultiplier);
            material.SetVector(IdPanelRect, new Vector4(rect.xMin, rect.yMin,
                Mathf.Max(0.0001f, rect.width), Mathf.Max(0.0001f, rect.height)));
            material.SetTexture(IdOverlayTex, panelImage != null ? panelImage.texture : Texture2D.whiteTexture);
            material.SetVector(IdOverlayRect, imageRect);
            material.SetVector(IdOverlayUV, imageUV);
            material.SetColor(IdOverlayColor, imageColor);
            material.SetFloat(IdOverlayEnabled, panelImage != null ? 1f : 0f);
            material.SetFloat(IdMaskWithImage, maskWithPanelImage ? 1f : 0f);
            material.SetColor(IdTint, tint);
            material.SetFloat(IdCornerRadius, cornerRadius);
            material.SetFloat(IdBorderWidth, borderWidth);
            material.SetColor(IdBorderColor, borderColor);
        }

        static void DestroySafe<T>(ref T obj) where T : Object
        {
            if (obj == null) return;
            DestroySafe((Object)obj);
            obj = null;
        }

        static void DestroySafe(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}

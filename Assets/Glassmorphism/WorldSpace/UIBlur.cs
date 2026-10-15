using UnityEngine;
using UnityEngine.UI;

namespace Glassmorphism
{
    public enum UIBlurSource
    {
        ScreenCapture,
        SharedBackdrop
    }

    /// Single panel-facing API. Rendering remains in two independent backend components.
    /// Screen capture: UIBlurScreenCapture. Shared camera/phone backdrop: UIBlurSharedBackdrop.
    [ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(RawImage), typeof(Mask))]
    [AddComponentMenu("Glassmorphism/UI Blur")]
    public sealed class UIBlur : MonoBehaviour
    {
        [Tooltip("Screen Capture includes background UI from the main camera, then draws the panel and foreground. Shared Backdrop blurs the scene camera output. XR stereo always uses Shared Backdrop.")]
        public UIBlurSource source = UIBlurSource.ScreenCapture;
        public Camera sourceCamera;

        [Tooltip("Screen Capture only. Canvas containing the UI behind this panel.")]
        public Canvas sourceCanvas;
        [Range(1, 8)] public int downsample = 2;
        [Range(0, 8)] public float blurSize = 1;
        [Range(1, 6)] public int blurIterations = 1;
        public bool hdrCapture;

        public Sprite panelImage;
        public UIBlurImageType imageType;
        [Tooltip("Sliced sprites: higher values make the sprite borders thinner.")]
        [Min(0.01f)] public float pixelsPerUnitMultiplier = 1;
        [Tooltip("Decorative image color and opacity; independent of the sprite mask.")]
        public Color imageColor = Color.white;
        [Tooltip("Fit the sprite in Simple mode. Sliced sprites fill the panel rect.")]
        public bool preserveImageAspect = true;
        [Tooltip("Clip this panel and child UI to the sprite silhouette.")]
        public bool maskWithPanelImage = true;
        [Tooltip("Alpha controls how strongly the tint covers the backdrop.")]
        public Color tint = Color.clear;
        [Min(0)] public float cornerRadius;
        [Min(0)] public float borderWidth;
        public Color borderColor = new Color(1, 1, 1, 0.5f);

        // Keep both shader references serialized so changing source also works in builds.
        [SerializeField] Shader captureBlurShader;
        [SerializeField] Shader capturePanelShader;
        [SerializeField] Shader backdropPanelShader;
        [SerializeField, HideInInspector] UIBlurScreenCapture captureModule;
        [SerializeField, HideInInspector] UIBlurSharedBackdrop backdropModule;
        [SerializeField, HideInInspector] bool initialized;

        RawImage panelGraphic;
        Camera lastCaptureCamera;
        Canvas lastCaptureCanvas;
        bool captureConfigured;

        void Reset()
        {
            AdoptExistingModule();
            ResolveShaders();
        }

        void OnEnable()
        {
            panelGraphic = GetComponent<RawImage>();
            if (!initialized) AdoptExistingModule();
            ResolveShaders();
            Synchronize();
        }

        void OnValidate()
        {
            ResolveShaders();
#if UNITY_EDITOR
            // Component creation and enable/disable callbacks must not run in OnValidate.
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void LateUpdate() => Synchronize();

        void ResolveShaders()
        {
            if (captureBlurShader == null) captureBlurShader = Shader.Find("Glassmorphism/SeparableBlur");
            if (capturePanelShader == null) capturePanelShader = Shader.Find("Glassmorphism/GlassUIPanel");
            if (backdropPanelShader == null) backdropPanelShader = Shader.Find("Glassmorphism/WorldSpaceGlass");
        }

        // Used on first attachment and by asset migration. Existing backend components
        // retain their script GUIDs/file IDs, so external serialized references survive.
        void AdoptExistingModule()
        {
            captureModule = GetComponent<UIBlurScreenCapture>();
            backdropModule = GetComponent<UIBlurSharedBackdrop>();
            if (captureModule != null)
            {
                source = UIBlurSource.ScreenCapture;
                sourceCamera = captureModule.sourceCamera;
                sourceCanvas = captureModule.sourceCanvas;
                downsample = captureModule.downsample;
                blurSize = captureModule.blurSize;
                blurIterations = captureModule.blurIterations;
                hdrCapture = captureModule.hdrCapture;
                captureBlurShader = captureModule.blurShader;
                capturePanelShader = captureModule.panelShader;
                CopyAppearanceFrom(captureModule);
            }
            else if (backdropModule != null)
            {
                source = UIBlurSource.SharedBackdrop;
                sourceCamera = backdropModule.sourceCamera;
                backdropPanelShader = backdropModule.panelShader;
                CopyAppearanceFrom(backdropModule);
            }
            initialized = true;
        }

        /// Push settings before backend LateUpdate/Canvas rendering. Only the selected
        /// backend is enabled; switching disables the old one before touching the new one.
        public void Synchronize()
        {
            if (!isActiveAndEnabled) return;
            bool capture = !UsesSharedBackdrop;
            if (capture)
            {
                if (backdropModule != null && backdropModule.enabled) backdropModule.enabled = false;
                if (captureModule == null)
                {
                    captureModule = GetComponent<UIBlurScreenCapture>();
                    if (captureModule == null) captureModule = gameObject.AddComponent<UIBlurScreenCapture>();
                    captureConfigured = false;
                }
                ConfigureCapture();
                if (!captureModule.enabled) captureModule.enabled = true;
            }
            else
            {
                if (captureModule != null && captureModule.enabled) captureModule.enabled = false;
                if (backdropModule == null)
                {
                    backdropModule = GetComponent<UIBlurSharedBackdrop>();
                    if (backdropModule == null) backdropModule = gameObject.AddComponent<UIBlurSharedBackdrop>();
                }
                ConfigureBackdrop();
                if (!backdropModule.enabled) backdropModule.enabled = true;
            }
            // Both remain hidden when a panel has previously used both source modes.
            if (captureModule != null && (captureModule.hideFlags & HideFlags.HideInInspector) == 0)
                captureModule.hideFlags |= HideFlags.HideInInspector;
            if (backdropModule != null && (backdropModule.hideFlags & HideFlags.HideInInspector) == 0)
                backdropModule.hideFlags |= HideFlags.HideInInspector;
        }

        /// XR always uses the shared renderer path. A per-panel mono capture camera
        /// cannot supply the independent views required by stereo rendering.
        public bool UsesSharedBackdrop
        {
            get
            {
                if (source == UIBlurSource.SharedBackdrop) return true;
                if (panelGraphic == null) panelGraphic = GetComponent<RawImage>();
                Camera camera = sourceCamera;
                if (camera == null && panelGraphic.canvas != null)
                    camera = panelGraphic.canvas.worldCamera;
                if (camera == null) camera = Camera.main;
                return camera != null && camera.stereoEnabled;
            }
        }

        // MODULE 1: main-camera background UI capture. Matching panel quality settings share one blur.
        void ConfigureCapture()
        {
            // Let the capture module retain its auto-resolved camera/canvas until the
            // user changes the corresponding input; don't trigger another canvas search.
            if (!captureConfigured || sourceCamera != lastCaptureCamera)
                captureModule.sourceCamera = sourceCamera;
            if (!captureConfigured || sourceCanvas != lastCaptureCanvas)
                captureModule.sourceCanvas = sourceCanvas;
            lastCaptureCamera = sourceCamera;
            lastCaptureCanvas = sourceCanvas;
            captureConfigured = true;
            captureModule.downsample = downsample;
            captureModule.blurSize = blurSize;
            captureModule.blurIterations = blurIterations;
            captureModule.hdrCapture = hdrCapture;
            captureModule.blurShader = captureBlurShader;
            captureModule.panelShader = capturePanelShader;
            CopyAppearanceTo(captureModule);
        }

        // MODULE 2: shared backdrop. Quality belongs to UIBlurRendererFeature
        // and is never duplicated per individual panel.
        void ConfigureBackdrop()
        {
            backdropModule.sourceCamera = sourceCamera;
            backdropModule.panelShader = backdropPanelShader;
            CopyAppearanceTo(backdropModule);
        }

        void OnDisable()
        {
            if (captureModule != null && captureModule.enabled) captureModule.enabled = false;
            if (backdropModule != null && backdropModule.enabled) backdropModule.enabled = false;
        }

        void OnDestroy()
        {
            // Expose the disabled compatibility components if only the facade is removed.
            if (captureModule != null) captureModule.hideFlags &= ~HideFlags.HideInInspector;
            if (backdropModule != null) backdropModule.hideFlags &= ~HideFlags.HideInInspector;
        }

        void CopyAppearanceFrom(UIBlurScreenCapture module)
        {
            panelImage = module.panelImage;
            imageType = module.imageType;
            pixelsPerUnitMultiplier = module.pixelsPerUnitMultiplier;
            imageColor = module.imageColor;
            preserveImageAspect = module.preserveImageAspect;
            maskWithPanelImage = module.maskWithPanelImage;
            tint = module.tint;
            cornerRadius = module.cornerRadius;
            borderWidth = module.borderWidth;
            borderColor = module.borderColor;
        }

        void CopyAppearanceTo(UIBlurScreenCapture module)
        {
            module.panelImage = panelImage;
            module.imageType = imageType;
            module.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
            module.imageColor = imageColor;
            module.preserveImageAspect = preserveImageAspect;
            module.maskWithPanelImage = maskWithPanelImage;
            module.tint = tint;
            module.cornerRadius = cornerRadius;
            module.borderWidth = borderWidth;
            module.borderColor = borderColor;
        }

        void CopyAppearanceFrom(UIBlurSharedBackdrop module)
        {
            panelImage = module.panelImage;
            imageType = module.imageType;
            pixelsPerUnitMultiplier = module.pixelsPerUnitMultiplier;
            imageColor = module.imageColor;
            preserveImageAspect = module.preserveImageAspect;
            maskWithPanelImage = module.maskWithPanelImage;
            tint = module.tint;
            cornerRadius = module.cornerRadius;
            borderWidth = module.borderWidth;
            borderColor = module.borderColor;
        }

        void CopyAppearanceTo(UIBlurSharedBackdrop module)
        {
            module.panelImage = panelImage;
            module.imageType = imageType;
            module.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;
            module.imageColor = imageColor;
            module.preserveImageAspect = preserveImageAspect;
            module.maskWithPanelImage = maskWithPanelImage;
            module.tint = tint;
            module.cornerRadius = cornerRadius;
            module.borderWidth = borderWidth;
            module.borderColor = borderColor;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Glassmorphism
{
    /// Frosted-glass backdrop blur for uGUI.
    /// Captures the Screen Space - Camera UI at its native resolution, blurs a
    /// downsampled copy, and samples this panel's region via continuous RawImage.uvRect —
    /// so scrollers don't jitter from integer crop snapping / 1-frame lag.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage))]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(Mask))]
    [AddComponentMenu("Glassmorphism/Glass UI Blur")]
    public sealed class GlassUIBlur : MonoBehaviour
    {
        [Tooltip("Gameplay / UI camera. Leave empty to use Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("Canvas that holds the UI to blur (behind this panel). Leave empty to auto-find.")]
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
        public GlassImageType imageType = GlassImageType.Simple;
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
        [Tooltip("Override blur shader. Leave null to auto-load Glassmorphism/SeparableBlur.")]
        public Shader blurShader;
        [Tooltip("UI shader that combines the backdrop, panel image, tint and border.")]
        public Shader panelShader;

        RawImage rawImage;
        RectTransform rectTransform;
        Camera blurCamera;
        Material blurMaterial;
        readonly GlassBlurKernel blurKernel = new GlassBlurKernel();
        Material panelMaterial;
        Material previousMaterial;
        RenderTexture fullRt;
        RenderTexture blurRtA;
        RenderTexture blurRtB;
        Vector2Int currentFullSize;
        Vector2Int currentBlurSize;
        RenderTextureFormat currentFormat;
        static readonly int IdDownsampleFootprint = Shader.PropertyToID("_DownsampleFootprint");
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
        readonly List<Graphic> panelGraphics = new List<Graphic>();
        readonly List<Graphic> hiddenGraphics = new List<Graphic>();

        void OnValidate()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnEnable()
        {
            rawImage = GetComponent<RawImage>();
            previousMaterial = rawImage.material;
            Mask mask = GetComponent<Mask>();
            if (mask == null) mask = gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            rectTransform = GetComponent<RectTransform>();
            ResolveSourceCanvas();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnDisable()
        {
            if (rawImage != null && (rawImage.texture == fullRt || rawImage.texture == blurRtA))
            {
                rawImage.texture = null;
                rawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            }

            ReleaseRenderTextures();
            if (rawImage != null && rawImage.material == panelMaterial)
                rawImage.material = previousMaterial;
            DestroySafe(ref blurCamera);
            DestroySafe(ref blurMaterial);
            DestroySafe(ref panelMaterial);
        }

        void LateUpdate()
        {
            Canvas.ForceUpdateCanvases();
            UpdateBlur();
            ApplyPanelAppearance();
        }

        void UpdateBlur()
        {
            Camera cam = sourceCamera != null ? sourceCamera : Camera.main;
            if (cam == null || rawImage == null) return;

            ResolveSourceCanvas();
            if (sourceCanvas == null) return;

            EnsureScreenSpaceCamera(cam);
            EnsureBlurCamera();
            EnsureBlurMaterial();
            if (blurMaterial == null) return;

            if (!TryGetScreenRect(cam, out Rect screenRect)) return;
            if (screenRect.width < 2f || screenRect.height < 2f) return;

            int maxRt = SystemInfo.maxTextureSize;
            int fullW = Mathf.Clamp(cam.pixelWidth, 2, maxRt);
            int fullH = Mathf.Clamp(cam.pixelHeight, 2, maxRt);
            int blurW = Mathf.Max(2, fullW / Mathf.Max(1, downsample));
            int blurH = Mathf.Max(2, fullH / Mathf.Max(1, downsample));
            EnsureRenderTextures(fullW, fullH, blurW, blurH,
                hdrCapture ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);

            Camera previousWorldCamera = sourceCanvas.worldCamera;
            try
            {
                // Foreground content must stay sharp and must not enter the backdrop.
                GetComponentsInChildren(false, panelGraphics);
                hiddenGraphics.Clear();
                foreach (Graphic graphic in panelGraphics)
                {
                    if (!graphic.enabled) continue;
                    hiddenGraphics.Add(graphic);
                    graphic.enabled = false;
                }
                sourceCanvas.worldCamera = blurCamera;
                ConfigureBlurCamera(cam);
                blurCamera.Render();
            }
            finally
            {
                sourceCanvas.worldCamera = previousWorldCamera;
                foreach (Graphic graphic in hiddenGraphics)
                {
                    if (graphic != null) graphic.enabled = true;
                }
                hiddenGraphics.Clear();
                Canvas.ForceUpdateCanvases();
            }

            // Filter the whole destination pixel footprint before dropping pixels.
            // A single bilinear sample aliases thin lines at large reduction ratios.
            blurMaterial.SetVector(IdDownsampleFootprint,
                new Vector4((float)fullW / blurW, (float)fullH / blurH, 0, 0));
            Graphics.Blit(fullRt, blurRtA, blurMaterial, 2);
            blurKernel.Apply(blurMaterial, blurSize);
            for (int i = 0; i < blurIterations; i++)
            {
                Graphics.Blit(blurRtA, blurRtB, blurMaterial, 0);
                Graphics.Blit(blurRtB, blurRtA, blurMaterial, 1);
            }

            // Continuous UVs — no integer CopyTexture crop, so scroll motion stays smooth.
            float screenW = Mathf.Max(1f, cam.pixelWidth);
            float screenH = Mathf.Max(1f, cam.pixelHeight);
            Rect viewport = cam.pixelRect;
            float u0 = (screenRect.xMin - viewport.xMin) / screenW;
            float u1 = (screenRect.xMax - viewport.xMin) / screenW;
            float v0 = (screenRect.yMin - viewport.yMin) / screenH;
            float v1 = (screenRect.yMax - viewport.yMin) / screenH;

            if (rawImage.texture != blurRtA)
                rawImage.texture = blurRtA;
            // Screen space and RenderTexture UVs both use bottom-left origin.
            Rect uvRect = new Rect(u0, v0, u1 - u0, v1 - v0);
            if (rawImage.uvRect != uvRect)
                rawImage.uvRect = uvRect;
        }

        void ResolveSourceCanvas()
        {
            if (sourceCanvas != null) return;

            Canvas own = GetComponentInParent<Canvas>();
            if (own != null && own.renderMode == RenderMode.ScreenSpaceCamera)
            {
                sourceCanvas = own.rootCanvas != null ? own.rootCanvas : own;
                return;
            }

            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas c = canvases[i];
                if (c != null && c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceCamera)
                {
                    sourceCanvas = c;
                    return;
                }
            }

            if (own != null)
                sourceCanvas = own.rootCanvas != null ? own.rootCanvas : own;
        }

        void EnsureScreenSpaceCamera(Camera cam)
        {
            if (sourceCanvas.renderMode != RenderMode.ScreenSpaceCamera || sourceCanvas.worldCamera == null)
            {
                sourceCanvas.renderMode = RenderMode.ScreenSpaceCamera;
                sourceCanvas.worldCamera = cam;
                if (sourceCanvas.planeDistance < 0.1f)
                    sourceCanvas.planeDistance = 1f;
            }

            if (sourceCamera == null)
                sourceCamera = sourceCanvas.worldCamera != null ? sourceCanvas.worldCamera : cam;
        }

        void ConfigureBlurCamera(Camera cam)
        {
            blurCamera.CopyFrom(cam);
            blurCamera.enabled = false;
            blurCamera.targetTexture = fullRt;
            blurCamera.cullingMask = cam.cullingMask;
            blurCamera.rect = new Rect(0f, 0f, 1f, 1f);
            blurCamera.allowMSAA = false;
            blurCamera.allowHDR = hdrCapture && cam.allowHDR;
            blurCamera.depth = cam.depth - 1;
            blurCamera.clearFlags = cam.clearFlags;
            blurCamera.backgroundColor = cam.backgroundColor;
            blurCamera.projectionMatrix = cam.projectionMatrix;
            blurCamera.fieldOfView = cam.fieldOfView;
            blurCamera.orthographic = cam.orthographic;
            blurCamera.orthographicSize = cam.orthographicSize;
            blurCamera.nearClipPlane = cam.nearClipPlane;
            blurCamera.farClipPlane = cam.farClipPlane;
            blurCamera.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
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

        void EnsureBlurCamera()
        {
            if (blurCamera != null) return;
            var go = new GameObject("~GlassUIBlurCam") { hideFlags = HideFlags.HideAndDontSave };
            blurCamera = go.AddComponent<Camera>();
            blurCamera.enabled = false;
        }

        void EnsureBlurMaterial()
        {
            if (blurMaterial != null) return;
            Shader shader = blurShader != null ? blurShader : Shader.Find("Glassmorphism/SeparableBlur");
            if (shader == null)
            {
                Debug.LogError("[GlassUIBlur] Shader 'Glassmorphism/SeparableBlur' not found.", this);
                return;
            }
            blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
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
            Vector4 imageRect = new Vector4(0f, 0f, 1f, 1f);
            if (panelImage != null && !GlassImageSlicing.IsSliced(panelImage, imageType)
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
            ApplyPanelUniforms(panelMaterial, rect, imageRect, imageUV);
            // A Mask creates a stencil material copy. Keep that actual drawing material in sync.
            Material renderingMaterial = rawImage.materialForRendering;
            if (renderingMaterial != panelMaterial)
                ApplyPanelUniforms(renderingMaterial, rect, imageRect, imageUV);
            if (rawImage.canvasRenderer.popMaterialCount > 0)
            {
                Material popMaterial = rawImage.canvasRenderer.GetPopMaterial(0);
                if (popMaterial != null)
                    ApplyPanelUniforms(popMaterial, rect, imageRect, imageUV);
            }
        }

        void ApplyPanelUniforms(Material material, Rect rect, Vector4 imageRect, Vector4 imageUV)
        {
            GlassImageSlicing.Apply(material, panelImage, imageType, rect, rawImage.canvas, pixelsPerUnitMultiplier);
            material.SetVector(IdPanelRect, new Vector4(rect.xMin, rect.yMin,
                Mathf.Max(0.0001f, rect.width), Mathf.Max(0.0001f, rect.height)));
            Rect uv = rawImage.uvRect;
            material.SetVector(IdBackdropUV, new Vector4(uv.x, uv.y,
                Mathf.Max(0.0001f, uv.width), Mathf.Max(0.0001f, uv.height)));
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

        void EnsureRenderTextures(int fullW, int fullH, int blurW, int blurH, RenderTextureFormat format)
        {
            if (fullRt != null && blurRtA != null && blurRtB != null &&
                currentFullSize.x == fullW && currentFullSize.y == fullH &&
                currentBlurSize.x == blurW && currentBlurSize.y == blurH && currentFormat == format) return;
            ReleaseRenderTextures();

            var desc = new RenderTextureDescriptor(fullW, fullH, format, 24)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true,
            };
            fullRt = new RenderTexture(desc)
            {
                name = "GlassUIBlur_Full",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            fullRt.Create();

            desc.width = blurW;
            desc.height = blurH;
            desc.depthBufferBits = 0;
            blurRtA = new RenderTexture(desc)
            {
                name = "GlassUIBlur_A",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            blurRtB = new RenderTexture(desc)
            {
                name = "GlassUIBlur_B",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            blurRtA.Create();
            blurRtB.Create();
            currentFullSize = new Vector2Int(fullW, fullH);
            currentBlurSize = new Vector2Int(blurW, blurH);
            currentFormat = format;
        }

        void ReleaseRenderTextures()
        {
            if (fullRt != null) { fullRt.Release(); DestroySafe(fullRt); fullRt = null; }
            if (blurRtA != null) { blurRtA.Release(); DestroySafe(blurRtA); blurRtA = null; }
            if (blurRtB != null) { blurRtB.Release(); DestroySafe(blurRtB); blurRtB = null; }
            currentFullSize = default;
            currentBlurSize = default;
        }

        static void DestroySafe<T>(ref T obj) where T : Object
        {
            if (obj == null) return;
            if (obj is Camera cam)
            {
                DestroySafe(cam.gameObject);
                obj = null;
                return;
            }
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

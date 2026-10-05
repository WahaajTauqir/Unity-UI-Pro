using UnityEngine;
using UnityEngine.UI;

namespace Glassmorphism
{
    /// Frosted-glass backdrop blur for uGUI.
    /// Renders the Screen Space - Camera UI behind this panel into an RT,
    /// crops to this element's screen rect, blurs with Glassmorphism/SeparableBlur,
    /// and assigns the result to a RawImage.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage))]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("Glassmorphism/Glass UI Blur")]
    public sealed class GlassUIBlur : MonoBehaviour
    {
        [Tooltip("Gameplay / UI camera. Leave empty to use Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("Canvas that holds the UI to blur (behind this panel). Leave empty to auto-find.")]
        public Canvas sourceCanvas;

        [Range(1, 8)] public int downsample = 2;
        [Tooltip("Kept for GlassPanel parity. UI frost strength is driven by Blur Iterations.")]
        [Range(0f, 8f)] public float blurSize = 3f;
        [Tooltip("How many downsample/upsample passes to run. Higher = frostier.")]
        [Range(1, 6)] public int blurIterations = 4;

        [Tooltip("Override blur shader. Leave null to auto-load Glassmorphism/SeparableBlur.")]
        public Shader blurShader;

        RawImage rawImage;
        RectTransform rectTransform;
        Camera blurCamera;
        Material blurMaterial;
        RenderTexture fullRt;
        RenderTexture rtA;
        RenderTexture rtB;
        Vector2Int currentFullSize;
        Vector2Int currentPanelSize;

        void OnEnable()
        {
            rawImage = GetComponent<RawImage>();
            rectTransform = GetComponent<RectTransform>();
            ResolveSourceCanvas();
        }

        void OnDisable()
        {
            if (rawImage != null && (rawImage.texture == rtA || rawImage.texture == rtB))
                rawImage.texture = null;

            ReleaseRenderTextures();
            DestroySafe(ref blurCamera);
            DestroySafe(ref blurMaterial);
        }

        void LateUpdate()
        {
            Camera cam = sourceCamera != null ? sourceCamera : Camera.main;
            if (cam == null || rawImage == null) return;

            ResolveSourceCanvas();
            if (sourceCanvas == null) return;

            EnsureScreenSpaceCamera(cam);
            EnsureBlurCamera();

            if (!TryGetScreenRect(cam, out Rect screenRect)) return;
            if (screenRect.width < 2f || screenRect.height < 2f) return;

            int maxRt = SystemInfo.maxTextureSize;
            int fullW = Mathf.Clamp(cam.pixelWidth / Mathf.Max(1, downsample), 2, maxRt);
            int fullH = Mathf.Clamp(cam.pixelHeight / Mathf.Max(1, downsample), 2, maxRt);
            EnsureFullRt(fullW, fullH);

            // Hide this panel so same-canvas capture does not blur itself.
            bool wasEnabled = rawImage.enabled;
            rawImage.enabled = false;

            Camera previousWorldCamera = sourceCanvas.worldCamera;
            sourceCanvas.worldCamera = blurCamera;

            ConfigureBlurCamera(cam);
            blurCamera.Render();

            sourceCanvas.worldCamera = previousWorldCamera;
            rawImage.enabled = wasEnabled;

            // Crop this panel's screen region out of the full capture.
            float screenW = Mathf.Max(1f, cam.pixelWidth);
            float screenH = Mathf.Max(1f, cam.pixelHeight);
            float u0 = Mathf.Clamp01(screenRect.xMin / screenW);
            float u1 = Mathf.Clamp01(screenRect.xMax / screenW);
            float v0 = Mathf.Clamp01(screenRect.yMin / screenH);
            float v1 = Mathf.Clamp01(screenRect.yMax / screenH);

            int cropX = Mathf.Clamp(Mathf.RoundToInt(u0 * fullW), 0, fullW - 1);
            int cropY = Mathf.Clamp(Mathf.RoundToInt(v0 * fullH), 0, fullH - 1);
            int cropW = Mathf.Clamp(Mathf.RoundToInt((u1 - u0) * fullW), 1, fullW - cropX);
            int cropH = Mathf.Clamp(Mathf.RoundToInt((v1 - v0) * fullH), 1, fullH - cropY);
            EnsurePanelRts(cropW, cropH);

            Graphics.CopyTexture(fullRt, 0, 0, cropX, cropY, cropW, cropH, rtA, 0, 0, 0, 0);
            ApplyDownsampleBlur(rtA, rtB, blurIterations);

            if (rawImage.texture != rtA)
                rawImage.texture = rtA;
        }

        /// Soft frosted look via repeated bilinear downsample/upsample.
        /// Avoids custom blit shaders, which are fragile across URP / Graphics.Blit paths.
        static void ApplyDownsampleBlur(RenderTexture target, RenderTexture scratch, int iterations)
        {
            int passes = Mathf.Max(1, iterations);
            RenderTexture current = target;

            for (int i = 0; i < passes; i++)
            {
                int w = Mathf.Max(2, current.width / 2);
                int h = Mathf.Max(2, current.height / 2);
                RenderTexture down = RenderTexture.GetTemporary(w, h, 0, current.format);
                down.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, down);
                if (current != target)
                    RenderTexture.ReleaseTemporary(current);
                current = down;
            }

            while (current.width < target.width || current.height < target.height)
            {
                int w = Mathf.Min(target.width, current.width * 2);
                int h = Mathf.Min(target.height, current.height * 2);
                if (w == current.width && h == current.height)
                    break;

                RenderTexture up = RenderTexture.GetTemporary(w, h, 0, current.format);
                up.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, up);
                RenderTexture.ReleaseTemporary(current);
                current = up;
            }

            Graphics.Blit(current, target);
            if (current != target)
                RenderTexture.ReleaseTemporary(current);

            // Keep scratch allocated for size stability; unused in this path.
            scratch.DiscardContents();
        }

        void ResolveSourceCanvas()
        {
            if (sourceCanvas != null) return;

            // Prefer a sibling/parent Screen Space - Camera canvas that is not our own overlay host.
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
            blurCamera.allowHDR = cam.allowHDR;
            blurCamera.depth = cam.depth - 1;
            blurCamera.clearFlags = cam.clearFlags;
            blurCamera.backgroundColor = cam.backgroundColor;
            blurCamera.ResetProjectionMatrix();
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
            Vector3[] corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);

            // Overlay canvases must use a null camera; Camera canvases use their world camera.
            Canvas ownCanvas = GetComponentInParent<Canvas>();
            Camera eventCam = null;
            if (ownCanvas != null && ownCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                eventCam = ownCanvas.worldCamera != null ? ownCanvas.worldCamera : cam;

            Vector2 s0 = RectTransformUtility.WorldToScreenPoint(eventCam, corners[0]);
            Vector2 s1 = RectTransformUtility.WorldToScreenPoint(eventCam, corners[1]);
            Vector2 s2 = RectTransformUtility.WorldToScreenPoint(eventCam, corners[2]);
            Vector2 s3 = RectTransformUtility.WorldToScreenPoint(eventCam, corners[3]);

            float xMin = Mathf.Min(Mathf.Min(s0.x, s1.x), Mathf.Min(s2.x, s3.x));
            float xMax = Mathf.Max(Mathf.Max(s0.x, s1.x), Mathf.Max(s2.x, s3.x));
            float yMin = Mathf.Min(Mathf.Min(s0.y, s1.y), Mathf.Min(s2.y, s3.y));
            float yMax = Mathf.Max(Mathf.Max(s0.y, s1.y), Mathf.Max(s2.y, s3.y));

            // Clamp to the source camera pixel rect so crop UVs stay valid.
            xMin = Mathf.Clamp(xMin, 0f, cam.pixelWidth);
            xMax = Mathf.Clamp(xMax, 0f, cam.pixelWidth);
            yMin = Mathf.Clamp(yMin, 0f, cam.pixelHeight);
            yMax = Mathf.Clamp(yMax, 0f, cam.pixelHeight);

            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return rect.width >= 2f && rect.height >= 2f;
        }

        void EnsureBlurCamera()
        {
            if (blurCamera != null) return;
            var go = new GameObject("~GlassUIBlurCam") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, worldPositionStays: false);
            blurCamera = go.AddComponent<Camera>();
            blurCamera.enabled = false;
        }

        void EnsureBlurMaterial()
        {
            if (blurMaterial != null) return;
            Shader s = blurShader != null ? blurShader : Shader.Find("Glassmorphism/SeparableBlur");
            if (s == null)
            {
                Debug.LogError("[GlassUIBlur] Shader 'Glassmorphism/SeparableBlur' not found.");
                return;
            }
            blurMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
        }

        void EnsureFullRt(int w, int h)
        {
            if (fullRt != null && currentFullSize.x == w && currentFullSize.y == h) return;
            if (fullRt != null) { fullRt.Release(); DestroySafe(fullRt); fullRt = null; }

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 24)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true,
            };
            fullRt = new RenderTexture(desc) { name = "GlassUIBlur_Full", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            fullRt.Create();
            currentFullSize = new Vector2Int(w, h);
        }

        void EnsurePanelRts(int w, int h)
        {
            if (rtA != null && rtB != null && currentPanelSize.x == w && currentPanelSize.y == h) return;
            ReleasePanelRts();

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 24)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true,
            };
            rtA = new RenderTexture(desc) { name = "GlassUIBlur_A", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            rtB = new RenderTexture(desc) { name = "GlassUIBlur_B", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            rtA.Create();
            rtB.Create();
            currentPanelSize = new Vector2Int(w, h);
        }

        void ReleasePanelRts()
        {
            if (rtA != null) { rtA.Release(); DestroySafe(rtA); rtA = null; }
            if (rtB != null) { rtB.Release(); DestroySafe(rtB); rtB = null; }
            currentPanelSize = default;
        }

        void ReleaseRenderTextures()
        {
            if (fullRt != null) { fullRt.Release(); DestroySafe(fullRt); fullRt = null; }
            currentFullSize = default;
            ReleasePanelRts();
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

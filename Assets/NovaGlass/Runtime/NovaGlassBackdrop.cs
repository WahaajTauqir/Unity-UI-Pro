using Nova;
using UnityEngine;

namespace NovaGlass
{
    /// Drop-in glassmorphism / frosted-glass backdrop for a Nova UIBlock2D.
    /// Renders an off-axis blurred view of the world behind the panel and
    /// assigns it as the block's image, so Nova's own rounded corners, tint
    /// (body Color), border and shadow do the rest of the work.
    [ExecuteAlways]
    [RequireComponent(typeof(UIBlock2D))]
    [AddComponentMenu("Nova Glass/Glass Backdrop")]
    [HelpURL("https://novaui.io/manual/UIBlock2D.html")]
    public sealed class NovaGlassBackdrop : MonoBehaviour
    {
        [Tooltip("The camera the player views the scene through. Leave empty to use Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("Render the backdrop at 1/N of the panel's on-screen pixel size. Higher = cheaper and softer.")]
        [Range(1, 8)] public int downsample = 4;

        [Tooltip("Blur radius in source-texel units, per iteration.")]
        [Range(0f, 8f)] public float blurSize = 2f;

        [Tooltip("How many H+V blur passes to run. Each doubles the effective radius.")]
        [Range(1, 6)] public int blurIterations = 2;

        [Tooltip("Layers excluded from the captured view. Put your Nova UI layer here so panels don't blur themselves.")]
        public LayerMask excludeLayers = 0;

        [Tooltip("Override the blur shader. Leave null to auto-load 'NovaGlass/SeparableBlur'.")]
        public Shader blurShader;

        UIBlock2D block;
        Camera blurCamera;
        Material blurMaterial;
        RenderTexture rtA;
        RenderTexture rtB;
        Vector2Int currentRtSize;

        void OnEnable()
        {
            block = GetComponent<UIBlock2D>();
        }

        void OnDisable()
        {
            if (block != null && block.RenderTexture != null && (block.RenderTexture == rtA || block.RenderTexture == rtB))
            {
                block.ClearImage();
            }

            ReleaseRenderTextures();

            if (blurCamera != null)
            {
                if (Application.isPlaying) Destroy(blurCamera.gameObject);
                else DestroyImmediate(blurCamera.gameObject);
                blurCamera = null;
            }

            if (blurMaterial != null)
            {
                if (Application.isPlaying) Destroy(blurMaterial);
                else DestroyImmediate(blurMaterial);
                blurMaterial = null;
            }
        }

        void LateUpdate()
        {
            Camera cam = sourceCamera != null ? sourceCamera : Camera.main;
            if (cam == null || block == null) return;

            EnsureBlurCamera();
            EnsureBlurMaterial();
            if (blurMaterial == null) return;

            if (!TryGetScreenRect(cam, out Rect screenRect)) return;
            if (screenRect.width < 2f || screenRect.height < 2f) return;

            int rtW = Mathf.Max(2, Mathf.RoundToInt(screenRect.width  / downsample));
            int rtH = Mathf.Max(2, Mathf.RoundToInt(screenRect.height / downsample));
            EnsureRenderTextures(rtW, rtH);

            ConfigureBlurCamera(cam, screenRect);
            blurCamera.Render();

            blurMaterial.SetFloat("_BlurSize", blurSize);
            RenderTexture src = rtA;
            RenderTexture dst = rtB;
            for (int i = 0; i < blurIterations; i++)
            {
                Graphics.Blit(src, dst, blurMaterial, 0); // horizontal
                Graphics.Blit(dst, src, blurMaterial, 1); // vertical
            }

            if (block.RenderTexture != src)
            {
                block.SetImage(src);
            }
        }

        void ConfigureBlurCamera(Camera cam, Rect screenRect)
        {
            blurCamera.CopyFrom(cam);
            blurCamera.enabled         = false;
            blurCamera.targetTexture   = rtA;
            blurCamera.cullingMask     = cam.cullingMask & ~excludeLayers.value;
            blurCamera.rect            = new Rect(0f, 0f, 1f, 1f);
            blurCamera.clearFlags      = cam.clearFlags;
            blurCamera.backgroundColor = cam.backgroundColor;
            blurCamera.allowMSAA       = false;
            blurCamera.allowHDR        = cam.allowHDR;
            blurCamera.depth           = cam.depth - 1;
            blurCamera.projectionMatrix = BuildSubFrustum(cam, screenRect);
            blurCamera.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
        }

        bool TryGetScreenRect(Camera cam, out Rect rect)
        {
            Vector2 size = block.CalculatedSize.XY.Value;
            Vector2 half = size * 0.5f;
            Transform t = block.transform;

            Vector3 c0 = t.TransformPoint(new Vector3(-half.x, -half.y, 0f));
            Vector3 c1 = t.TransformPoint(new Vector3( half.x, -half.y, 0f));
            Vector3 c2 = t.TransformPoint(new Vector3( half.x,  half.y, 0f));
            Vector3 c3 = t.TransformPoint(new Vector3(-half.x,  half.y, 0f));

            Vector3 s0 = cam.WorldToScreenPoint(c0);
            Vector3 s1 = cam.WorldToScreenPoint(c1);
            Vector3 s2 = cam.WorldToScreenPoint(c2);
            Vector3 s3 = cam.WorldToScreenPoint(c3);

            if (s0.z <= 0f || s1.z <= 0f || s2.z <= 0f || s3.z <= 0f)
            {
                rect = default;
                return false;
            }

            float xMin = Mathf.Min(Mathf.Min(s0.x, s1.x), Mathf.Min(s2.x, s3.x));
            float xMax = Mathf.Max(Mathf.Max(s0.x, s1.x), Mathf.Max(s2.x, s3.x));
            float yMin = Mathf.Min(Mathf.Min(s0.y, s1.y), Mathf.Min(s2.y, s3.y));
            float yMax = Mathf.Max(Mathf.Max(s0.y, s1.y), Mathf.Max(s2.y, s3.y));
            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        static Matrix4x4 BuildSubFrustum(Camera cam, Rect screenRect)
        {
            float screenW = Mathf.Max(1, cam.pixelWidth);
            float screenH = Mathf.Max(1, cam.pixelHeight);

            float u0 = Mathf.Clamp01(screenRect.xMin / screenW);
            float u1 = Mathf.Clamp01(screenRect.xMax / screenW);
            float v0 = Mathf.Clamp01(screenRect.yMin / screenH);
            float v1 = Mathf.Clamp01(screenRect.yMax / screenH);

            float near = cam.nearClipPlane;
            float far  = cam.farClipPlane;

            if (cam.orthographic)
            {
                float orthoH = cam.orthographicSize;
                float orthoW = orthoH * cam.aspect;
                float left   = Mathf.Lerp(-orthoW, +orthoW, u0);
                float right  = Mathf.Lerp(-orthoW, +orthoW, u1);
                float bottom = Mathf.Lerp(-orthoH, +orthoH, v0);
                float top    = Mathf.Lerp(-orthoH, +orthoH, v1);
                return Matrix4x4.Ortho(left, right, bottom, top, near, far);
            }
            else
            {
                float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float fullTop    = near * tanHalfFov;
                float fullRight  = fullTop * cam.aspect;
                float left   = Mathf.Lerp(-fullRight, +fullRight, u0);
                float right  = Mathf.Lerp(-fullRight, +fullRight, u1);
                float bottom = Mathf.Lerp(-fullTop,   +fullTop,   v0);
                float top    = Mathf.Lerp(-fullTop,   +fullTop,   v1);
                return Matrix4x4.Frustum(left, right, bottom, top, near, far);
            }
        }

        void EnsureBlurCamera()
        {
            if (blurCamera != null) return;
            var go = new GameObject("~NovaGlassBlurCam") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, worldPositionStays: false);
            blurCamera = go.AddComponent<Camera>();
            blurCamera.enabled = false;
        }

        void EnsureBlurMaterial()
        {
            if (blurMaterial != null) return;
            Shader s = blurShader != null ? blurShader : Shader.Find("NovaGlass/SeparableBlur");
            if (s == null)
            {
                Debug.LogError("[NovaGlass] Shader 'NovaGlass/SeparableBlur' not found. Make sure NovaGlassBlur.shader compiled.");
                return;
            }
            blurMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
        }

        void EnsureRenderTextures(int w, int h)
        {
            if (rtA != null && rtB != null && currentRtSize.x == w && currentRtSize.y == h) return;
            ReleaseRenderTextures();

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 0)
            {
                msaaSamples       = 1,
                useMipMap         = false,
                autoGenerateMips  = false,
                sRGB              = true,
            };

            rtA = new RenderTexture(desc) { name = "NovaGlass_A", hideFlags = HideFlags.HideAndDontSave };
            rtB = new RenderTexture(desc) { name = "NovaGlass_B", hideFlags = HideFlags.HideAndDontSave };
            rtA.Create();
            rtB.Create();
            currentRtSize = new Vector2Int(w, h);
        }

        void ReleaseRenderTextures()
        {
            if (rtA != null) { rtA.Release(); if (Application.isPlaying) Destroy(rtA); else DestroyImmediate(rtA); rtA = null; }
            if (rtB != null) { rtB.Release(); if (Application.isPlaying) Destroy(rtB); else DestroyImmediate(rtB); rtB = null; }
            currentRtSize = default;
        }
    }
}

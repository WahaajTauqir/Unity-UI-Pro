using UnityEngine;
using UnityEngine.Rendering;

namespace Glassmorphism
{
    /// Drop-in glassmorphism panel. No Nova, no uGUI — just a world-space quad with
    /// a rounded-rect SDF shader that samples a per-panel blurred backdrop.
    ///
    /// One off-axis camera renders only the slice of the world behind this panel into a
    /// downsampled RT, two separable Gaussian passes blur it, the result is fed into the
    /// panel shader as _MainTex. Panel world size is taken from transform.lossyScale.xy.
    [ExecuteAlways]
    [AddComponentMenu("Glassmorphism/Glass Panel")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class GlassPanel : MonoBehaviour
    {
        [Header("Capture")]
        [Tooltip("Camera the player views the scene through. Leave empty to use Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("Layers excluded from the captured backdrop. Add glass-panel layer(s) so panels don't blur themselves.")]
        public LayerMask excludeLayers = 0;

        [Header("Blur")]
        [Range(1, 8)]  public int   downsample      = 4;
        [Range(0f, 8f)] public float blurSize       = 2f;
        [Range(1, 6)]  public int   blurIterations  = 2;

        [Header("Look")]
        [ColorUsage(true, false)] public Color tint        = new Color(1f, 1f, 1f, 0.25f);
        [ColorUsage(true, false)] public Color borderColor = new Color(1f, 1f, 1f, 0.5f);
        [Min(0f)] public float borderWidth  = 0.01f;
        [Min(0f)] public float cornerRadius = 0.1f;

        [Header("Shaders (auto)")]
        public Shader panelShader;
        public Shader blurShader;

        MeshFilter   meshFilter;
        MeshRenderer meshRenderer;
        Mesh         quadMesh;
        Material     panelMaterial;
        Material     blurMaterial;
        Camera       blurCamera;
        RenderTexture rtA;
        RenderTexture rtB;
        Vector2Int   currentRtSize;

        static readonly int IdMainTex       = Shader.PropertyToID("_MainTex");
        static readonly int IdTintColor     = Shader.PropertyToID("_TintColor");
        static readonly int IdBorderColor   = Shader.PropertyToID("_BorderColor");
        static readonly int IdBorderWidth   = Shader.PropertyToID("_BorderWidth");
        static readonly int IdCornerRadius  = Shader.PropertyToID("_CornerRadius");
        static readonly int IdSize          = Shader.PropertyToID("_Size");
        static readonly int IdBlurSize      = Shader.PropertyToID("_BlurSize");

        void OnEnable()
        {
            EnsureMesh();
            EnsureRenderer();
            EnsurePanelMaterial();
        }

        void OnDisable()
        {
            ReleaseRenderTextures();
            DestroySafe(ref blurCamera);
            DestroySafe(ref blurMaterial);
            // keep mesh/material so the user sees the panel in the inspector;
            // they'll be regenerated on next enable if needed.
        }

        void LateUpdate()
        {
            Camera cam = sourceCamera != null ? sourceCamera : Camera.main;
            if (cam == null) return;

            EnsurePanelMaterial();
            EnsureBlurCamera();
            EnsureBlurMaterial();

            Vector2 worldSize = new Vector2(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y));

            ApplyPanelUniforms(worldSize);

            if (blurMaterial == null) return;

            if (!TryGetScreenRect(cam, worldSize, out Rect screenRect)) return;
            if (screenRect.width < 2f || screenRect.height < 2f) return;

            int rtW = Mathf.Max(2, Mathf.RoundToInt(screenRect.width  / downsample));
            int rtH = Mathf.Max(2, Mathf.RoundToInt(screenRect.height / downsample));
            EnsureRenderTextures(rtW, rtH);

            ConfigureBlurCamera(cam, screenRect);
            blurCamera.Render();

            blurMaterial.SetFloat(IdBlurSize, blurSize);
            RenderTexture src = rtA;
            RenderTexture dst = rtB;
            for (int i = 0; i < blurIterations; i++)
            {
                Graphics.Blit(src, dst, blurMaterial, 0); // horizontal
                Graphics.Blit(dst, src, blurMaterial, 1); // vertical
            }

            panelMaterial.SetTexture(IdMainTex, src);
        }

        void ApplyPanelUniforms(Vector2 worldSize)
        {
            if (panelMaterial == null) return;
            panelMaterial.SetVector(IdSize,         new Vector4(worldSize.x, worldSize.y, 0f, 0f));
            panelMaterial.SetColor (IdTintColor,    tint);
            panelMaterial.SetColor (IdBorderColor,  borderColor);
            panelMaterial.SetFloat (IdBorderWidth,  borderWidth);
            panelMaterial.SetFloat (IdCornerRadius, cornerRadius);
        }

        void ConfigureBlurCamera(Camera cam, Rect screenRect)
        {
            blurCamera.CopyFrom(cam);
            blurCamera.enabled         = false;
            blurCamera.targetTexture   = rtA;
            blurCamera.cullingMask     = cam.cullingMask & ~excludeLayers.value;
            blurCamera.rect            = new Rect(0f, 0f, 1f, 1f);
            blurCamera.allowMSAA       = false;
            blurCamera.allowHDR        = cam.allowHDR;
            blurCamera.depth           = cam.depth - 1;
            blurCamera.projectionMatrix = BuildSubFrustum(cam, screenRect);
            blurCamera.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
        }

        bool TryGetScreenRect(Camera cam, Vector2 worldSize, out Rect rect)
        {
            Vector2 half = worldSize * 0.5f;
            Transform t  = transform;

            Vector3 c0 = t.TransformPoint(new Vector3(-0.5f, -0.5f, 0f));
            Vector3 c1 = t.TransformPoint(new Vector3( 0.5f, -0.5f, 0f));
            Vector3 c2 = t.TransformPoint(new Vector3( 0.5f,  0.5f, 0f));
            Vector3 c3 = t.TransformPoint(new Vector3(-0.5f,  0.5f, 0f));

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
            float screenW = Mathf.Max(1f, cam.pixelWidth);
            float screenH = Mathf.Max(1f, cam.pixelHeight);

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

        void EnsureMesh()
        {
            meshFilter = GetComponent<MeshFilter>();
            if (quadMesh == null)
            {
                quadMesh = new Mesh
                {
                    name      = "GlassPanelQuad",
                    hideFlags = HideFlags.HideAndDontSave,
                    vertices  = new[]
                    {
                        new Vector3(-0.5f, -0.5f, 0f),
                        new Vector3( 0.5f, -0.5f, 0f),
                        new Vector3( 0.5f,  0.5f, 0f),
                        new Vector3(-0.5f,  0.5f, 0f),
                    },
                    uv = new[]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0f),
                        new Vector2(1f, 1f),
                        new Vector2(0f, 1f),
                    },
                    triangles = new[] { 0, 2, 1, 0, 3, 2 },
                };
                quadMesh.RecalculateBounds();
            }
            if (meshFilter.sharedMesh != quadMesh) meshFilter.sharedMesh = quadMesh;
        }

        void EnsureRenderer()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows    = false;
            meshRenderer.lightProbeUsage   = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        void EnsurePanelMaterial()
        {
            if (panelMaterial == null)
            {
                Shader s = panelShader != null ? panelShader : Shader.Find("Glassmorphism/GlassPanel");
                if (s == null)
                {
                    Debug.LogError("[Glassmorphism] Shader 'Glassmorphism/GlassPanel' not found.");
                    return;
                }
                panelMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (meshRenderer != null && meshRenderer.sharedMaterial != panelMaterial)
            {
                meshRenderer.sharedMaterial = panelMaterial;
            }
        }

        void EnsureBlurMaterial()
        {
            if (blurMaterial != null) return;
            Shader s = blurShader != null ? blurShader : Shader.Find("Glassmorphism/SeparableBlur");
            if (s == null)
            {
                Debug.LogError("[Glassmorphism] Shader 'Glassmorphism/SeparableBlur' not found.");
                return;
            }
            blurMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
        }

        void EnsureBlurCamera()
        {
            if (blurCamera != null) return;
            var go = new GameObject("~GlassPanelBlurCam") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, worldPositionStays: false);
            blurCamera = go.AddComponent<Camera>();
            blurCamera.enabled = false;
        }

        void EnsureRenderTextures(int w, int h)
        {
            if (rtA != null && rtB != null && currentRtSize.x == w && currentRtSize.y == h) return;
            ReleaseRenderTextures();

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.DefaultHDR, 0)
            {
                msaaSamples      = 1,
                useMipMap        = false,
                autoGenerateMips = false,
                sRGB             = true,
            };

            rtA = new RenderTexture(desc) { name = "GlassPanel_A", hideFlags = HideFlags.HideAndDontSave };
            rtB = new RenderTexture(desc) { name = "GlassPanel_B", hideFlags = HideFlags.HideAndDontSave };
            rtA.Create();
            rtB.Create();
            currentRtSize = new Vector2Int(w, h);
        }

        void ReleaseRenderTextures()
        {
            if (rtA != null) { rtA.Release(); DestroySafe(rtA); rtA = null; }
            if (rtB != null) { rtB.Release(); DestroySafe(rtB); rtB = null; }
            currentRtSize = default;
        }

        static void DestroySafe<T>(ref T obj) where T : Object
        {
            if (obj == null) return;
            if (obj is Camera cam) { DestroySafe(cam.gameObject); obj = null; return; }
            DestroySafe(obj);
            obj = null;
        }

        static void DestroySafe(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }
    }
}

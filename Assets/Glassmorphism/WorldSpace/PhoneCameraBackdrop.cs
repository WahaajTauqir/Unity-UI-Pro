using System.Collections;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Glassmorphism
{
    /// Live phone camera preview and one shared GPU blur for overlay glass.
    /// No AR packages, camera readbacks, or secondary render camera are required.
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(RawImage))]
    [AddComponentMenu("Glassmorphism/Phone Camera Backdrop")]
    public sealed class PhoneCameraBackdrop : MonoBehaviour
    {
        [Tooltip("Disable only for static preview/testing without opening a camera.")]
        public bool startCameraOnPlay = true;
        public bool preferFrontCamera;
        public bool mirrorFrontCamera = true;
        [Min(16)] public int requestedWidth = 1280;
        [Min(16)] public int requestedHeight = 720;
        [Range(15, 60)] public int requestedFPS = 30;
        [Header("Shared Blur")]
        [Range(1, 8)] public int downsample = 4;
        [Range(0, 8)] public float blurSize = 1;
        [Range(1, 6)] public int blurIterations = 1;
        [Header("Preview")]
        [Tooltip("Optional static preview while editing or waiting for the camera. It is not a live camera image.")]
        public Texture previewTexture;
        public Text statusLabel;
        public Shader cameraShader;
        public Shader blurShader;

        public Texture BlurredTexture => blurA;
        public bool IsReady => isActiveAndEnabled && blurA != null && blurA.IsCreated() && lastInput != null;

        RawImage preview;
        WebCamTexture cameraTexture;
        bool frontCamera, paused;
        float cameraStartedAt;
        Material cameraMaterial, blurMaterial, previousMaterial;
        Texture previousTexture;
        RenderTexture blurA, blurB;
        readonly GlassBlurKernel kernel = new GlassBlurKernel();
        Texture lastInput;
        int lastWidth, lastHeight, lastRotation, lastDownsample, lastIterations;
        bool lastVerticalMirror, lastFrontMirror;
        float lastBlurSize = -1;
        string status;
        static readonly int UV0 = Shader.PropertyToID("_CameraUV0");
        static readonly int UV1 = Shader.PropertyToID("_CameraUV1");
        static readonly int PixelSize = Shader.PropertyToID("_OutputPixelSize");

        void Reset() => ResolveShaders();
        void OnValidate()
        {
            ResolveShaders();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void ResolveShaders()
        {
            if (cameraShader == null) cameraShader = Shader.Find("Glassmorphism/PhoneCameraFeed");
            if (blurShader == null) blurShader = Shader.Find("Glassmorphism/SeparableBlur");
        }

        void OnEnable()
        {
            preview = GetComponent<RawImage>();
            previousMaterial = preview.material;
            previousTexture = preview.texture;
            ResolveShaders();
            SetStatus(Application.isPlaying && startCameraOnPlay ? "Starting camera…" : "Preview image · Camera starts in Play mode");
            if (Application.isPlaying && startCameraOnPlay) StartCoroutine(StartCamera());
        }

        IEnumerator StartCamera()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                bool completed = false;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => completed = true;
                callbacks.PermissionDenied += _ => completed = true;
                callbacks.PermissionRequestDismissed += _ => completed = true;
                SetStatus("Allow camera access to start the preview.");
                Permission.RequestUserPermission(Permission.Camera, callbacks);
                while (!completed) yield return null;
                if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                {
                    SetStatus("Camera access denied. Enable it in system settings and reopen the app.");
                    yield break;
                }
            }
#elif UNITY_IOS || UNITY_EDITOR || UNITY_STANDALONE_OSX || UNITY_WEBGL
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                SetStatus("Camera access denied. Enable it in system settings and reopen the app.");
                yield break;
            }
#endif
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                SetStatus("No camera found. Connect a webcam or run this scene on your phone.");
                yield break;
            }
            WebCamDevice device = devices[0];
            foreach (var candidate in devices)
                if (candidate.isFrontFacing == preferFrontCamera) { device = candidate; break; }
            frontCamera = device.isFrontFacing;
            cameraTexture = new WebCamTexture(device.name, Mathf.Max(16, requestedWidth),
                Mathf.Max(16, requestedHeight), requestedFPS);
            cameraTexture.Play();
            cameraStartedAt = Time.realtimeSinceStartup;
            SetStatus("Starting camera…");
            while (cameraTexture != null && (cameraTexture.width <= 16 || !cameraTexture.didUpdateThisFrame))
            {
                if (!paused && Time.realtimeSinceStartup - cameraStartedAt > 15)
                {
                    cameraTexture.Stop();
                    SetStatus("No camera frames received. Check camera access and reopen the app.");
                    yield break;
                }
                yield return null;
            }
        }

        void LateUpdate() => RefreshPreview();

        public void RefreshPreview()
        {
            if (preview == null) return;
            bool live = Application.isPlaying && !paused && cameraTexture != null
                && cameraTexture.isPlaying && cameraTexture.width > 16;
            Texture input = live ? cameraTexture : previewTexture;
            if (input == null) return;
            int rotation = live ? cameraTexture.videoRotationAngle : 0;
            bool verticalMirror = live && cameraTexture.videoVerticallyMirrored;
            bool horizontalMirror = live && frontCamera && mirrorFrontCamera;
            Rect viewport = preview.canvas != null ? preview.canvas.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            int width = Mathf.Max(2, Mathf.RoundToInt(viewport.width));
            int height = Mathf.Max(2, Mathf.RoundToInt(viewport.height));
            int reduction = Mathf.Clamp(downsample, 1, 8);
            bool changed = input != lastInput || width != lastWidth || height != lastHeight
                || rotation != lastRotation || verticalMirror != lastVerticalMirror || horizontalMirror != lastFrontMirror
                || reduction != lastDownsample || blurSize != lastBlurSize || blurIterations != lastIterations
                || blurA == null || blurB == null || !blurA.IsCreated() || !blurB.IsCreated()
                || blurMaterial == null || blurMaterial.GetFloat("_BlurCenterWeight") <= 0;
            if (!changed && (!live || !cameraTexture.didUpdateThisFrame)) return;
            if (cameraMaterial == null || blurMaterial == null)
            {
                ResolveShaders();
                if (cameraShader == null || blurShader == null) { SetStatus("Camera preview shaders are missing."); return; }
                if (cameraMaterial == null) cameraMaterial = new Material(cameraShader) { hideFlags = HideFlags.HideAndDontSave };
                if (blurMaterial == null) blurMaterial = new Material(blurShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            GetCameraUV(input.width, input.height, width, height, rotation, verticalMirror, horizontalMirror,
                out Vector4 row0, out Vector4 row1);
            cameraMaterial.SetVector(UV0, row0);
            cameraMaterial.SetVector(UV1, row1);
            preview.texture = input;
            preview.material = cameraMaterial;
            int blurWidth = Mathf.Max(2, width / reduction), blurHeight = Mathf.Max(2, height / reduction);
            EnsureTextures(blurWidth, blurHeight);
            cameraMaterial.SetVector(PixelSize, new Vector4(1f / blurWidth, 1f / blurHeight, 0, 0));
            Graphics.Blit(input, blurA, cameraMaterial, 1);
            kernel.Apply(blurMaterial, blurSize);
            for (int i = 0; i < Mathf.Clamp(blurIterations, 1, 6); i++)
            {
                Graphics.Blit(blurA, blurB, blurMaterial, 0);
                Graphics.Blit(blurB, blurA, blurMaterial, 1);
            }
            lastInput = input; lastWidth = width; lastHeight = height;
            lastRotation = rotation; lastVerticalMirror = verticalMirror; lastFrontMirror = horizontalMirror;
            lastDownsample = reduction; lastBlurSize = blurSize; lastIterations = blurIterations;
            if (live) SetStatus("");
        }

        /// Maps screen UVs to the raw camera, using aspect-fill without stretching.
        public static void GetCameraUV(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
            int rotation, bool verticallyMirrored, bool horizontallyMirrored, out Vector4 row0, out Vector4 row1)
        {
            float radians = rotation * Mathf.Deg2Rad;
            bool sideways = rotation % 180 != 0;
            float sourceAspect = sideways ? (float)sourceHeight / Mathf.Max(1, sourceWidth)
                : (float)sourceWidth / Mathf.Max(1, sourceHeight);
            float targetAspect = (float)outputWidth / Mathf.Max(1, outputHeight);
            Vector2 crop = sourceAspect > targetAspect ? new Vector2(targetAspect / sourceAspect, 1)
                : new Vector2(1, sourceAspect / targetAspect);
            if (horizontallyMirrored) crop.x *= -1;
            Vector2 x = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * crop.x;
            Vector2 y = new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians)) * crop.y;
            Vector2 origin = Vector2.one * 0.5f - (x + y) * 0.5f;
            if (verticallyMirrored) { x.y *= -1; y.y *= -1; origin.y = 1 - origin.y; }
            row0 = new Vector4(x.x, y.x, origin.x, 0);
            row1 = new Vector4(x.y, y.y, origin.y, 0);
        }

        void EnsureTextures(int width, int height)
        {
            if (blurA != null && blurB != null && blurA.width == width && blurA.height == height
                && blurA.IsCreated() && blurB.IsCreated()) return;
            ReleaseTextures();
            blurA = MakeTexture(width, height, "PhoneCameraBlur_A");
            blurB = MakeTexture(width, height, "PhoneCameraBlur_B");
        }

        static RenderTexture MakeTexture(int width, int height, string name)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false
            };
            texture.Create();
            return texture;
        }

        void SetStatus(string value)
        {
            if (status == value) return;
            status = value;
            if (statusLabel == null) return;
            statusLabel.text = value;
            statusLabel.gameObject.SetActive(!string.IsNullOrEmpty(value));
        }

        void OnApplicationPause(bool pause)
        {
            paused = pause;
            if (cameraTexture == null) return;
            if (pause) { cameraTexture.Stop(); SetStatus("Camera paused"); }
            else { cameraTexture.Play(); cameraStartedAt = Time.realtimeSinceStartup; lastInput = null; SetStatus("Starting camera…"); }
        }

        void OnDisable()
        {
            StopAllCoroutines();
            if (cameraTexture != null) { cameraTexture.Stop(); DestroyResource(cameraTexture); cameraTexture = null; }
            if (preview != null && preview.material == cameraMaterial)
            {
                preview.material = previousMaterial;
                preview.texture = previousTexture;
            }
            ReleaseTextures();
            DestroyResource(cameraMaterial); cameraMaterial = null;
            DestroyResource(blurMaterial); blurMaterial = null;
            lastInput = null;
        }

        void ReleaseTextures()
        {
            if (blurA != null) { blurA.Release(); DestroyResource(blurA); blurA = null; }
            if (blurB != null) { blurB.Release(); DestroyResource(blurB); blurB = null; }
        }

        static void DestroyResource(Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) Destroy(resource);
            else DestroyImmediate(resource);
        }
    }
}

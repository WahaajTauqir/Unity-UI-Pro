using System.Collections;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Glassmorphism
{
    /// Optional non-AR camera source. Draws a sharp background; owns no blur buffers.
    /// PhoneCameraBackgroundRendererFeature draws it before opaque scene rendering.
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Glassmorphism/Phone Camera Background")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Glassmorphism", "Glassmorphism.WorldSpace", "PhoneCameraBackdrop")]
    public sealed class PhoneCameraBackground : MonoBehaviour
    {
        public bool startCameraOnPlay = true;
        public bool preferFrontCamera;
        public bool mirrorFrontCamera = true;
        [Min(16)] public int requestedWidth = 1280;
        [Min(16)] public int requestedHeight = 720;
        [Range(15, 60)] public int requestedFPS = 30;
        [Tooltip("Leave empty when this component is attached to the viewing camera.")]
        public Camera targetCamera;
        [Tooltip("Static background while editing or waiting for the camera.")]
        public Texture previewTexture;
        public Text statusLabel;
        public Shader cameraShader;

        static readonly System.Collections.Generic.HashSet<PhoneCameraBackground> Sources =
            new System.Collections.Generic.HashSet<PhoneCameraBackground>();
        WebCamTexture cameraTexture;
        Camera attachedCamera;
        bool frontCamera, paused, mappingValid;
        float cameraStartedAt;
        Material cameraMaterial;
        Texture lastInput;
        int lastWidth, lastHeight, lastSourceWidth, lastSourceHeight, lastRotation;
        bool lastVerticalMirror, lastFrontMirror;
        string status;
        static readonly int CameraTexture = Shader.PropertyToID("_CameraTex");
        static readonly int UV0 = Shader.PropertyToID("_CameraUV0");
        static readonly int UV1 = Shader.PropertyToID("_CameraUV1");

        internal static PhoneCameraBackground ForCamera(Camera camera)
        {
            foreach (var source in Sources)
                if (source != null && source.isActiveAndEnabled
                    && (source.targetCamera != null ? source.targetCamera
                        : source.attachedCamera != null ? source.attachedCamera : Camera.main) == camera)
                    return source;
            return null;
        }

        void InvalidateMapping() => mappingValid = false;
        void Reset() => ResolveShader();
        void OnValidate()
        {
            InvalidateMapping();
            ResolveShader();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void ResolveShader()
        {
            if (cameraShader == null) cameraShader = Shader.Find("Glassmorphism/PhoneCameraFeed");
        }

        void OnEnable()
        {
            attachedCamera = GetComponent<Camera>();
            Sources.Add(this);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged += InvalidateMapping;
#endif
            InvalidateMapping();
            ResolveShader();
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

        // Called by the background pass, using this camera's actual render dimensions.
        internal Material PrepareBackground(Camera camera, int width, int height)
        {
            bool live = Application.isPlaying && !paused && cameraTexture != null
                && cameraTexture.isPlaying && cameraTexture.width > 16;
            Texture input = live ? cameraTexture : previewTexture;
            if (input == null) return null;
            ResolveShader();
            if (cameraShader == null) { SetStatus("Camera background shader is missing."); return null; }
            if (cameraMaterial != null && cameraMaterial.shader != cameraShader)
            {
                DestroyResource(cameraMaterial);
                cameraMaterial = null;
            }
            if (cameraMaterial == null)
            {
                cameraMaterial = new Material(cameraShader) { hideFlags = HideFlags.HideAndDontSave };
                mappingValid = false;
            }
            int rotation = live ? cameraTexture.videoRotationAngle : 0;
            bool verticalMirror = live && cameraTexture.videoVerticallyMirrored;
            bool horizontalMirror = live && frontCamera && mirrorFrontCamera;
            bool changed = !mappingValid || input != lastInput
                || input.width != lastSourceWidth || input.height != lastSourceHeight
                || width != lastWidth || height != lastHeight || rotation != lastRotation
                || verticalMirror != lastVerticalMirror || horizontalMirror != lastFrontMirror;
            if (changed)
            {
                GetCameraUV(input.width, input.height, width, height, rotation, verticalMirror, horizontalMirror,
                    out Vector4 row0, out Vector4 row1);
                cameraMaterial.SetTexture(CameraTexture, input);
                cameraMaterial.SetVector(UV0, row0);
                cameraMaterial.SetVector(UV1, row1);
                lastInput = input;
                lastSourceWidth = input.width; lastSourceHeight = input.height;
                lastWidth = width; lastHeight = height; lastRotation = rotation;
                lastVerticalMirror = verticalMirror; lastFrontMirror = horizontalMirror;
                mappingValid = true;
            }
            if (live) SetStatus("");
            return cameraMaterial;
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
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged -= InvalidateMapping;
#endif
            StopAllCoroutines();
            if (cameraTexture != null) { cameraTexture.Stop(); DestroyResource(cameraTexture); cameraTexture = null; }
            Sources.Remove(this);
            DestroyResource(cameraMaterial); cameraMaterial = null;
            lastInput = null;
            mappingValid = false;
        }

        static void DestroyResource(Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) Destroy(resource);
            else DestroyImmediate(resource);
        }
    }
}

using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Glassmorphism.Tests
{
    public sealed class UIBlurScreenCaptureTests
    {
        [Test]
        public void FirstFrameUsesOnlyMainCameraAndKeepsRuntimeChildrenSharp()
        {
            using (var fixture = new CaptureFixture())
            {
                int renders = 0;
                bool foregroundStayedEnabled = true;
                Action<ScriptableRenderContext, Camera> callback = (context, camera) =>
                {
                    renders++;
                    Assert.That(camera, Is.SameAs(fixture.Camera));
                    foregroundStayedEnabled &= fixture.Foreground.enabled;
                };
                RenderPipelineManager.beginCameraRendering += callback;
                try
                {
                    fixture.Capture();
                    var first = fixture.ReadOutput();
                    Assert.That(first[128 * 256 + 128].r, Is.GreaterThan(240), "Sharp red child must render on the first frame.");
                    fixture.Capture();
                    CollectionAssert.AreEqual(first, fixture.ReadOutput(), "No first-frame dropout or capture feedback.");
                    Assert.That(renders, Is.EqualTo(2), "One camera render per frame.");

                    var dynamicGraphic = fixture.EmptyChild.AddComponent<Image>();
                    dynamicGraphic.color = Color.blue;
                    fixture.Capture();
                    var changed = fixture.ReadOutput();
                    Assert.That(changed[128 * 256 + 128].b, Is.GreaterThan(240));
                    Assert.That(changed[128 * 256 + 170], Is.EqualTo(first[128 * 256 + 170]), "Foreground must not bleed into the blur.");
                    Assert.That(foregroundStayedEnabled, Is.True);
                    Assert.That(fixture.DisabledGraphic.enabled, Is.False);
                    Assert.That(dynamicGraphic.enabled, Is.True);

                    var parent = new GameObject("Reparented UI", typeof(RectTransform));
                    parent.transform.SetParent(fixture.Blur.transform, false);
                    dynamicGraphic.transform.SetParent(parent.transform, false);
                    fixture.Capture();
                    Assert.That(fixture.ReadOutput()[128 * 256 + 128].b, Is.GreaterThan(240));
                    dynamicGraphic.gameObject.SetActive(false);
                    fixture.Capture();
                    CollectionAssert.AreEqual(first, fixture.ReadOutput());
                    Assert.That(fixture.Canvas.worldCamera, Is.SameAs(fixture.Camera));
                    Assert.That(fixture.Blur.GetComponent<GraphicRaycaster>(), Is.Not.Null);
                }
                finally { RenderPipelineManager.beginCameraRendering -= callback; }
            }
        }

        [Test]
        public void MovingAndResizingPanelUpdatesAppearanceAndStencilMaterials()
        {
            using (var fixture = new CaptureFixture())
            {
                fixture.Capture();
                Rect oldUV = fixture.Image.uvRect;
                fixture.PanelRect.anchoredPosition += new Vector2(18, -10);
                fixture.PanelRect.sizeDelta = new Vector2(110, 90);
                fixture.Blur.cornerRadius = 7;
                fixture.Blur.borderWidth = 2;
                fixture.Blur.imageColor = new Color(.7f, .5f, .9f, .4f);
                fixture.Capture();

                Assert.That(fixture.Image.uvRect, Is.Not.EqualTo(oldUV));
                var material = fixture.Image.materialForRendering;
                Assert.That(material.GetFloat("_CornerRadius"), Is.EqualTo(7));
                Assert.That(material.GetFloat("_BorderWidth"), Is.EqualTo(2));
                Assert.That(material.GetColor("_OverlayColor"), Is.EqualTo(fixture.Blur.imageColor));
                Assert.That(material.GetVector("_PanelRect").z, Is.EqualTo(110));
                var uv = fixture.Image.uvRect;
                Assert.That(material.GetVector("_BackdropUV"), Is.EqualTo(new Vector4(uv.x, uv.y, uv.width, uv.height)));
                var backdropBefore = material.GetVector("_BackdropUV");

                // Recreated stencil copies need current state even when fields stay unchanged.
                fixture.Mask.enabled = false;
                fixture.Mask.enabled = true;
                fixture.Capture();
                material = fixture.Image.materialForRendering;
                Assert.That(material.GetFloat("_CornerRadius"), Is.EqualTo(7));
                Assert.That(material.GetVector("_BackdropUV"), Is.EqualTo(backdropBefore));
                if (fixture.Image.canvasRenderer.popMaterialCount > 0)
                {
                    var pop = fixture.Image.canvasRenderer.GetPopMaterial(0);
                    Assert.That(pop.GetFloat("_CornerRadius"), Is.EqualTo(7));
                    Assert.That(pop.GetVector("_BackdropUV"), Is.EqualTo(backdropBefore));
                }
            }
        }

        [Test]
        public void EqualQualitySharesCaptureAndChangingQualitySeparatesIt()
        {
            using (var fixture = new CaptureFixture())
            {
                var other = new GameObject("Other panel", typeof(RectTransform), typeof(RawImage));
                other.transform.SetParent(fixture.Canvas.transform, false);
                var panel = other.AddComponent<UIBlurScreenCapture>();
                panel.sourceCamera = fixture.Camera;
                panel.sourceCanvas = fixture.Canvas;
                panel.downsample = fixture.Blur.downsample;
                panel.blurSize = fixture.Blur.blurSize;
                panel.blurIterations = fixture.Blur.blurIterations;
                fixture.Capture();
                var image = other.GetComponent<RawImage>();
                Assert.That(image.material.GetFloat("_CaptureGroup"), Is.EqualTo(fixture.Image.material.GetFloat("_CaptureGroup")));
                panel.downsample = panel.downsample == 4 ? 2 : 4;
                fixture.Capture();
                Assert.That(image.material.GetFloat("_CaptureGroup"), Is.Not.EqualTo(fixture.Image.material.GetFloat("_CaptureGroup")));
                Assert.That(other.layer, Is.EqualTo(31));
                panel.enabled = false;
                Assert.That(other.layer, Is.EqualTo(0), "Restore the original layer when capture is disabled.");
            }
        }

        [Test]
        public void StagedForegroundRemainsClickable()
        {
            using (var fixture = new CaptureFixture())
            {
                fixture.Capture();
                var eventObject = new GameObject("Test event system", typeof(UnityEngine.EventSystems.EventSystem));
                SceneManager.MoveGameObjectToScene(eventObject, fixture.Camera.gameObject.scene);
                try
                {
                    var eventData = new UnityEngine.EventSystems.PointerEventData(eventObject.GetComponent<UnityEngine.EventSystems.EventSystem>())
                    {
                        position = new Vector2(128, 128)
                    };
                    var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                    fixture.Blur.GetComponent<GraphicRaycaster>().Raycast(eventData, results);
                    Assert.That(results.Exists(hit => hit.gameObject == fixture.Foreground.gameObject), Is.True);
                }
                finally { Object.DestroyImmediate(eventObject); }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CaptureBlurPreservesAlphaWithoutChangingSharedBackdrop(bool preserveAlpha)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires GPU rendering.");
            var source = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(.2f, .4f, .6f, .25f);
            source.SetPixels(pixels);
            source.Apply();
            var a = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var b = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            a.Create(); b.Create();
            var material = new Material(Shader.Find("Glassmorphism/WorldGlassBlur"));
            if (preserveAlpha) material.EnableKeyword("UIBLUR_PRESERVE_ALPHA");
            new UIBlurGaussianKernel().Apply(material, 2.5f);
            material.SetVector("_DownsampleTargetSize", new Vector4(1f / 8, 1f / 8, 0, 0));
            var cmd = new CommandBuffer();
            var active = RenderTexture.active;
            var read = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            try
            {
                cmd.SetRenderTarget(a);
                Blitter.BlitTexture(cmd, new RenderTargetIdentifier(source), new Vector4(1, 1, 0, 0), material, 0);
                cmd.SetRenderTarget(b);
                Blitter.BlitTexture(cmd, new RenderTargetIdentifier(a), new Vector4(1, 1, 0, 0), material, 1);
                cmd.SetRenderTarget(a);
                Blitter.BlitTexture(cmd, new RenderTargetIdentifier(b), new Vector4(1, 1, 0, 0), material, 2);
                Graphics.ExecuteCommandBuffer(cmd);
                RenderTexture.active = a;
                read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                read.Apply();
                var color = read.GetPixel(4, 4);
                Assert.That(color.r, Is.EqualTo(.2f).Within(.01f));
                Assert.That(color.g, Is.EqualTo(.4f).Within(.01f));
                Assert.That(color.b, Is.EqualTo(.6f).Within(.01f));
                Assert.That(color.a, Is.EqualTo(preserveAlpha ? .25f : 1f).Within(.01f));
            }
            finally
            {
                RenderTexture.active = active;
                cmd.Release();
                a.Release(); b.Release();
                Object.DestroyImmediate(a); Object.DestroyImmediate(b);
                Object.DestroyImmediate(read); Object.DestroyImmediate(material); Object.DestroyImmediate(source);
            }
        }

        sealed class CaptureFixture : IDisposable
        {
            static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            static readonly MethodInfo Update = typeof(UIBlurScreenCapture).GetMethod("LateUpdate", Private);
            readonly Scene scene;
            readonly GameObject root;
            readonly RenderTexture output;
            public readonly Camera Camera;
            public readonly Canvas Canvas;
            public readonly UIBlurScreenCapture Blur;
            public readonly RawImage Image;
            public readonly RectTransform PanelRect;
            public readonly Mask Mask;
            public readonly Image Foreground, DisabledGraphic;
            public readonly GameObject EmptyChild;

            public CaptureFixture()
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Assert.Ignore("Requires GPU UI rendering.");
                scene = EditorSceneManager.NewPreviewScene();
                root = new GameObject("Screen capture test");
                SceneManager.MoveGameObjectToScene(root, scene);
                var cameraGO = Child("Camera", root.transform, typeof(Camera));
                Camera = cameraGO.GetComponent<Camera>();
                Camera.enabled = false;
                Camera.scene = scene;
                Camera.transform.position = new Vector3(0, 0, -10);
                Camera.orthographic = true;
                Camera.orthographicSize = 128;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;
                Camera.allowHDR = false;
                Camera.allowMSAA = false;
                output = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                output.Create();
                Camera.targetTexture = output;
                var light = Child("Light", root.transform, typeof(Light)).GetComponent<Light>();
                light.type = LightType.Directional;
                light.enabled = false;
                Canvas = Child("Canvas", root.transform, typeof(Canvas)).GetComponent<Canvas>();
                Canvas.renderMode = RenderMode.ScreenSpaceCamera;
                Canvas.worldCamera = Camera;
                Canvas.planeDistance = 1;
                var background = Child("Background", Canvas.transform, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                background.color = new Color(.15f, .3f, .45f, 1);
                background.rectTransform.anchorMin = Vector2.zero;
                background.rectTransform.anchorMax = Vector2.one;
                background.rectTransform.offsetMin = background.rectTransform.offsetMax = Vector2.zero;
                var clip = Child("Clip", Canvas.transform, typeof(RectTransform), typeof(RectMask2D));
                ((RectTransform)clip.transform).sizeDelta = new Vector2(150, 180);
                var panel = Child("Panel", clip.transform, typeof(RectTransform), typeof(RawImage), typeof(Mask));
                PanelRect = (RectTransform)panel.transform;
                PanelRect.sizeDelta = new Vector2(160, 140);
                Image = panel.GetComponent<RawImage>();
                Mask = panel.GetComponent<Mask>();
                Blur = panel.AddComponent<UIBlurScreenCapture>();
                Blur.sourceCamera = Camera;
                Blur.sourceCanvas = Canvas;
                Blur.cornerRadius = 12;
                Foreground = Child("Foreground", panel.transform, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                Foreground.rectTransform.sizeDelta = new Vector2(30, 30);
                Foreground.color = Color.red;
                DisabledGraphic = Child("Disabled", panel.transform, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                DisabledGraphic.enabled = false;
                EmptyChild = Child("Nested transform", Foreground.transform, typeof(RectTransform));
                ((RectTransform)EmptyChild.transform).sizeDelta = new Vector2(20, 20);
            }

            static GameObject Child(string name, Transform parent, params Type[] components)
            {
                var child = new GameObject(name, components);
                child.transform.SetParent(parent, false);
                return child;
            }

            public void Capture()
            {
                Update.Invoke(Blur, null);
                Camera.Render();
            }

            public Color32[] ReadOutput()
            {
                var active = RenderTexture.active;
                var texture = output;
                var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                try
                {
                    RenderTexture.active = texture;
                    read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    read.Apply();
                    return read.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = active;
                    Object.DestroyImmediate(read);
                }
            }

            public void Dispose()
            {
                Object.DestroyImmediate(root);
                output.Release();
                Object.DestroyImmediate(output);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}

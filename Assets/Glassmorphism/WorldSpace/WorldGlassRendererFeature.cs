using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Glassmorphism
{
    /// Blurs the camera color after AR background/opaque rendering and before glass UI.
    /// One shared result per camera, independent of panel count. No secondary camera.
    public sealed class WorldGlassRendererFeature : ScriptableRendererFeature
    {
        [Range(1, 8)] public int downsample = 4;
        [Range(0, 8)] public float blurSize = 1;
        [Range(1, 4)] public int blurIterations = 1;
        public bool hdrCapture;
        [Tooltip("Assign Glassmorphism/WorldGlassBlur so it is included in builds.")]
        public Shader blurShader;

        Material material;
        BlurPass pass;

        public override void Create()
        {
            pass?.Dispose();
            CoreUtils.Destroy(material);
            if (blurShader == null) blurShader = Shader.Find("Glassmorphism/WorldGlassBlur");
            material = blurShader != null ? CoreUtils.CreateEngineMaterial(blurShader) : null;
            pass = new BlurPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            bool capture = camera.cameraType == CameraType.Game && !camera.stereoEnabled
                && WorldSpaceGlass.HasPanelsFor(camera) && material != null;
            pass.Setup(material, capture, camera.GetInstanceID(), downsample, blurSize, blurIterations, hdrCapture);
            // Even cameras without panels reset the globals, preventing another
            // camera's backdrop from leaking into a later view.
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            CoreUtils.Destroy(material);
        }

        sealed class BlurPass : ScriptableRenderPass
        {
            static readonly int Backdrop = Shader.PropertyToID("_WorldGlassBackdrop");
            static readonly int CameraID = Shader.PropertyToID("_WorldGlassCameraID");
            static readonly int DownsampleTargetSize = Shader.PropertyToID("_DownsampleTargetSize");
            readonly ProfilingSampler profiling = new ProfilingSampler("World Glass Backdrop");
            Material material;
            readonly GlassBlurKernel blurKernel = new GlassBlurKernel();
            bool capture, hdr;
            int cameraId, downsample, iterations;
            RTHandle a, b;

            public BlurPass() => renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

            public void Setup(Material material, bool capture, int cameraId, int downsample, float radius, int iterations, bool hdr)
            {
                this.material = material;
                this.capture = capture;
                this.cameraId = cameraId;
                this.downsample = Mathf.Clamp(downsample, 1, 8);
                this.iterations = Mathf.Clamp(iterations, 1, 4);
                this.hdr = hdr;
                requiresIntermediateTexture = capture;
                if (material != null) blurKernel.Apply(material, radius);
            }

            RenderTextureDescriptor BlurDescriptor(RenderTextureDescriptor descriptor)
            {
                descriptor.width = Mathf.Max(2, descriptor.width / downsample);
                descriptor.height = Mathf.Max(2, descriptor.height / downsample);
                descriptor.depthBufferBits = 0;
                descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.msaaSamples = 1;
                descriptor.bindMS = false;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                descriptor.useDynamicScale = false;
                if (!hdr)
                    descriptor.graphicsFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                        ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                material.SetVector(DownsampleTargetSize,
                    new Vector4(1f / descriptor.width, 1f / descriptor.height, 0, 0));
                return descriptor;
            }

            // URP compatibility mode. Blitter handles the source RTHandle scale/orientation.
#pragma warning disable CS0672, CS0618
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                if (!capture) return;
                var descriptor = BlurDescriptor(renderingData.cameraData.cameraTargetDescriptor);
                RenderingUtils.ReAllocateHandleIfNeeded(ref a, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "WorldGlass_A");
                RenderingUtils.ReAllocateHandleIfNeeded(ref b, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "WorldGlass_B");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, profiling))
                {
                    if (capture)
                    {
                        Blitter.BlitCameraTexture(cmd, renderingData.cameraData.renderer.cameraColorTargetHandle, a, material, 0);
                        for (int i = 0; i < iterations; i++)
                        {
                            Blitter.BlitCameraTexture(cmd, a, b, material, 1);
                            Blitter.BlitCameraTexture(cmd, b, a, material, 2);
                        }
                        cmd.SetGlobalTexture(Backdrop, a.nameID);
                        cmd.SetGlobalFloat(CameraID, cameraId);
                    }
                    else cmd.SetGlobalFloat(CameraID, 0);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0672, CS0618

            sealed class PublishData { public int cameraId; }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                TextureHandle result = graph.defaultResources.blackTexture;
                bool canCapture = capture && !resources.isActiveTargetBackBuffer;
                if (canCapture)
                {
                    var cameraData = frameData.Get<UniversalCameraData>();
                    var descriptor = BlurDescriptor(cameraData.cameraTargetDescriptor);
                    var first = UniversalRenderer.CreateRenderGraphTexture(graph, descriptor, "WorldGlass_A", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
                    var second = UniversalRenderer.CreateRenderGraphTexture(graph, descriptor, "WorldGlass_B", false, FilterMode.Bilinear, TextureWrapMode.Clamp);
                    graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(resources.activeColorTexture, first, material, 0), passName: "World Glass Downsample");
                    for (int i = 0; i < iterations; i++)
                    {
                        graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(first, second, material, 1), passName: "World Glass Horizontal");
                        graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(second, first, material, 2), passName: "World Glass Vertical");
                    }
                    result = first;
                }
                using (var builder = graph.AddRasterRenderPass<PublishData>("World Glass Publish", out var data))
                {
                    data.cameraId = canCapture ? cameraId : 0;
                    builder.UseTexture(result);
                    builder.SetGlobalTextureAfterPass(result, Backdrop);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PublishData data, RasterGraphContext context) => context.cmd.SetGlobalFloat(CameraID, data.cameraId));
                }
            }

            public void Dispose()
            {
                Shader.SetGlobalFloat(CameraID, 0);
                a?.Release();
                b?.Release();
                a = b = null;
            }
        }
    }
}

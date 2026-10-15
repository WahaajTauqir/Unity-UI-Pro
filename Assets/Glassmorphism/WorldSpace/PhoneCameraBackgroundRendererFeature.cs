using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Glassmorphism
{
    /// Optional phone-preview source adapter, analogous to AR Background Renderer Feature.
    /// Draws only the sharp camera feed. UIBlurRendererFeature owns every blur pass.
    public sealed class PhoneCameraBackgroundRendererFeature : ScriptableRendererFeature
    {
        BackgroundPass pass;
        public override void Create() => pass = new BackgroundPass();

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera.cameraType != CameraType.Game || camera.stereoEnabled) return;
            var source = PhoneCameraBackground.ForCamera(camera);
            if (source == null) return;
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            Material material = source.PrepareBackground(camera, descriptor.width, descriptor.height);
            if (material == null) return;
            pass.Setup(material);
            renderer.EnqueuePass(pass);
        }

        sealed class BackgroundPass : ScriptableRenderPass
        {
            Material material;
            static readonly Vector4 ScaleBias = new Vector4(1, 1, 0, 0);

            // Like an AR background pass, draw first so opaque virtual objects cover it.
            // The camera must use Solid Color clearing (not a later skybox).
            public BackgroundPass() => renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            public void Setup(Material value) => material = value;

#pragma warning disable CS0672, CS0618
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
                => ConfigureTarget(renderingData.cameraData.renderer.cameraColorTargetHandle);

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get("Phone Camera Background");
                Blitter.BlitTexture(cmd, ScaleBias, material, 0);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0672, CS0618

            sealed class PassData { public Material material; }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                using (var builder = graph.AddRasterRenderPass<PassData>("Phone Camera Background", out var data))
                {
                    data.material = material;
                    // The external WebCamTexture is bound by the source adapter. This
                    // pass writes the entire color target; depth remains untouched.
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                        Blitter.BlitTexture(context.cmd, ScaleBias, data.material, 0));
                }
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Glassmorphism
{
    /// Main camera only: transparent scene/background UI -> shared blur groups ->
    /// native foreground UI renderer list. Does not render the scene a second time.
    internal sealed class UIBlurScreenCapturePass : ScriptableRenderPass
    {
        struct Group
        {
            public int downsample, iterations;
            public float radius;
            public bool hdr;
            public bool Matches(UIBlurScreenCapture panel) =>
                downsample == Mathf.Clamp(panel.downsample, 1, 8)
                && iterations == Mathf.Clamp(panel.blurIterations, 0, 6)
                && radius.Equals(Mathf.Clamp(panel.blurSize, 0, 8)) && hdr == panel.hdrCapture;
        }

        static readonly int CameraID = Shader.PropertyToID("_UICaptureCameraID");
        static readonly int GroupCount = Shader.PropertyToID("_UICaptureGroupCount");
        static readonly int TargetSize = Shader.PropertyToID("_DownsampleTargetSize");
        static readonly int[] Backdrops = MakeBackdropIDs();
        static readonly List<ShaderTagId> Tags = new List<ShaderTagId>
        {
            new ShaderTagId("SRPDefaultUnlit"), new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly")
        };
        static readonly List<Group> stagedGroups = new List<Group>(UIBlurScreenCapture.MaxCaptureGroups);
        readonly List<Group> groups = new List<Group>(UIBlurScreenCapture.MaxCaptureGroups);
        readonly Material[] materials = new Material[UIBlurScreenCapture.MaxCaptureGroups];
        readonly UIBlurGaussianKernel[] kernels = new UIBlurGaussianKernel[UIBlurScreenCapture.MaxCaptureGroups];
        readonly RTHandle[] a = new RTHandle[UIBlurScreenCapture.MaxCaptureGroups];
        readonly RTHandle[] b = new RTHandle[UIBlurScreenCapture.MaxCaptureGroups];
        readonly TextureHandle[] results = new TextureHandle[UIBlurScreenCapture.MaxCaptureGroups];
        int cameraId, nativeWidth, nativeHeight;

        static int[] MakeBackdropIDs()
        {
            var ids = new int[UIBlurScreenCapture.MaxCaptureGroups];
            for (int i = 0; i < ids.Length; i++) ids[i] = Shader.PropertyToID("_UICaptureBackdrop" + i);
            return ids;
        }

        public UIBlurScreenCapturePass() => renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

        internal static void PreparePanels(Camera camera)
        {
            // Bind slots before camera culling builds native UI batches. Assigning a
            // new slot in AddRenderPasses alone is too late for the first visible frame.
            stagedGroups.Clear();
            foreach (var panel in UIBlurScreenCapture.Panels)
            {
                if (panel == null || !panel.CanCapture(camera)) continue;
                int slot = 0;
                while (slot < stagedGroups.Count && !stagedGroups[slot].Matches(panel)) slot++;
                if (slot == stagedGroups.Count && slot < UIBlurScreenCapture.MaxCaptureGroups)
                    stagedGroups.Add(new Group
                    {
                        downsample = Mathf.Clamp(panel.downsample, 1, 8),
                        iterations = Mathf.Clamp(panel.blurIterations, 0, 6),
                        radius = Mathf.Clamp(panel.blurSize, 0, 8), hdr = panel.hdrCapture
                    });
                panel.BindCaptureGroup(camera, slot);
            }
            if (stagedGroups.Count > 0) Canvas.ForceUpdateCanvases();
        }

        internal void Setup(Camera camera, Shader shader)
        {
            groups.Clear();
            cameraId = camera.GetInstanceID();
            nativeWidth = Mathf.Max(2, camera.pixelWidth);
            nativeHeight = Mathf.Max(2, camera.pixelHeight);
            if (camera.cameraType == CameraType.Game && shader != null)
            {
                foreach (var panel in UIBlurScreenCapture.Panels)
                {
                    if (panel == null || !panel.CanCapture(camera)) continue;
                    int slot = 0;
                    while (slot < groups.Count && !groups[slot].Matches(panel)) slot++;
                    if (slot == UIBlurScreenCapture.MaxCaptureGroups)
                    {
                        panel.BindCaptureGroup(camera, slot);
                        Debug.LogError("[UIBlur] At most eight different Screen Capture quality groups can be used by one camera.", panel);
                        continue;
                    }
                    if (slot == groups.Count)
                        groups.Add(new Group
                        {
                            downsample = Mathf.Clamp(panel.downsample, 1, 8),
                            iterations = Mathf.Clamp(panel.blurIterations, 0, 6),
                            radius = Mathf.Clamp(panel.blurSize, 0, 8), hdr = panel.hdrCapture
                        });
                    panel.BindCaptureGroup(camera, slot);
                }
            }
            requiresIntermediateTexture = groups.Count > 0;
            for (int i = 0; i < groups.Count; i++)
            {
                if (materials[i] != null && materials[i].shader != shader) CoreUtils.Destroy(materials[i]);
                if (materials[i] == null)
                {
                    materials[i] = CoreUtils.CreateEngineMaterial(shader);
                    materials[i].EnableKeyword("UIBLUR_PRESERVE_ALPHA");
                }
                if (kernels[i] == null) kernels[i] = new UIBlurGaussianKernel();
                // Each deferred group owns its material; later groups cannot overwrite
                // another group's radius/kernel before RenderGraph executes.
                kernels[i].Apply(materials[i], groups[i].radius);
            }
        }

        RenderTextureDescriptor Descriptor(RenderTextureDescriptor descriptor, int slot)
        {
            var group = groups[slot];
            // Keep the requested native-pixel blur resolution even when URP render
            // scale is below one; don't silently lower the old panel quality setting.
            descriptor.width = Mathf.Max(2, nativeWidth / group.downsample);
            descriptor.height = Mathf.Max(2, nativeHeight / group.downsample);
            descriptor.depthBufferBits = 0;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.useDynamicScale = false;
            descriptor.colorFormat = group.hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            descriptor.sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear;
            materials[slot].SetVector(TargetSize, new Vector4(1f / descriptor.width, 1f / descriptor.height, 0, 0));
            return descriptor;
        }

        sealed class PublishData { public int camera, count; }
        sealed class DrawData { public RendererListHandle list; }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var camera = frameData.Get<UniversalCameraData>();
            int count = resources.isActiveTargetBackBuffer ? 0 : groups.Count;
            for (int i = 0; i < count; i++)
            {
                var descriptor = Descriptor(camera.cameraTargetDescriptor, i);
                var first = UniversalRenderer.CreateRenderGraphTexture(graph, descriptor, "UI Capture Blur " + i, false, FilterMode.Bilinear, TextureWrapMode.Clamp);
                var second = UniversalRenderer.CreateRenderGraphTexture(graph, descriptor, "UI Capture Scratch " + i, false, FilterMode.Bilinear, TextureWrapMode.Clamp);
                graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(resources.activeColorTexture, first, materials[i], 0), passName: "UI Capture Downsample");
                for (int iteration = 0; groups[i].radius > 0 && iteration < groups[i].iterations; iteration++)
                {
                    graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(first, second, materials[i], 1), passName: "UI Capture Horizontal");
                    graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(second, first, materials[i], 2), passName: "UI Capture Vertical");
                }
                results[i] = first;
            }
            using (var builder = graph.AddRasterRenderPass<PublishData>("UI Capture Publish", out var data))
            {
                data.camera = count > 0 ? cameraId : 0;
                data.count = count;
                for (int i = 0; i < count; i++)
                {
                    builder.UseTexture(results[i]);
                    builder.SetGlobalTextureAfterPass(results[i], Backdrops[i]);
                }
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PublishData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalFloat(CameraID, data.camera);
                    context.cmd.SetGlobalFloat(GroupCount, data.count);
                });
            }
            if (count == 0) return;
            var rendering = frameData.Get<UniversalRenderingData>();
            var lights = frameData.Get<UniversalLightData>();
            var drawing = RenderingUtils.CreateDrawingSettings(Tags, rendering, camera, lights, SortingCriteria.CommonTransparent);
            var filtering = new FilteringSettings(RenderQueueRange.transparent, 1 << UIBlurScreenCapture.ForegroundLayer);
            var list = graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filtering));
            using (var builder = graph.AddRasterRenderPass<DrawData>("UI Capture Foreground", out var data))
            {
                data.list = list;
                builder.UseRendererList(list);
                for (int i = 0; i < count; i++) builder.UseTexture(results[i]);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc((DrawData data, RasterGraphContext context) => context.cmd.DrawRendererList(data.list));
            }
        }

#pragma warning disable CS0672, CS0618
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var descriptor = Descriptor(data.cameraData.cameraTargetDescriptor, i);
                RenderingUtils.ReAllocateHandleIfNeeded(ref a[i], descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "UI Capture Blur " + i);
                RenderingUtils.ReAllocateHandleIfNeeded(ref b[i], descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "UI Capture Scratch " + i);
            }
            ConfigureTarget(data.cameraData.renderer.cameraColorTargetHandle, data.cameraData.renderer.cameraDepthTargetHandle);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData data)
        {
            var cmd = CommandBufferPool.Get("UI Capture Main Camera");
            for (int i = 0; i < groups.Count; i++)
            {
                Blitter.BlitCameraTexture(cmd, data.cameraData.renderer.cameraColorTargetHandle, a[i], materials[i], 0);
                for (int iteration = 0; groups[i].radius > 0 && iteration < groups[i].iterations; iteration++)
                {
                    Blitter.BlitCameraTexture(cmd, a[i], b[i], materials[i], 1);
                    Blitter.BlitCameraTexture(cmd, b[i], a[i], materials[i], 2);
                }
                cmd.SetGlobalTexture(Backdrops[i], a[i].nameID);
            }
            cmd.SetGlobalFloat(CameraID, groups.Count > 0 ? cameraId : 0);
            cmd.SetGlobalFloat(GroupCount, groups.Count);
            if (groups.Count > 0) CoreUtils.SetRenderTarget(cmd, data.cameraData.renderer.cameraColorTargetHandle, data.cameraData.renderer.cameraDepthTargetHandle);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            if (groups.Count > 0)
            {
                var drawing = CreateDrawingSettings(Tags, ref data, SortingCriteria.CommonTransparent);
                var filtering = new FilteringSettings(RenderQueueRange.transparent, 1 << UIBlurScreenCapture.ForegroundLayer);
                context.DrawRenderers(data.cullResults, ref drawing, ref filtering);
            }
        }
#pragma warning restore CS0672, CS0618

        internal void Dispose()
        {
            Shader.SetGlobalFloat(CameraID, 0);
            Shader.SetGlobalFloat(GroupCount, 0);
            for (int i = 0; i < materials.Length; i++)
            {
                CoreUtils.Destroy(materials[i]);
                materials[i] = null;
                a[i]?.Release(); b[i]?.Release();
                a[i] = b[i] = null;
            }
        }
    }
}

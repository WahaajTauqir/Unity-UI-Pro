# VR / XR setup

UIBlur uses Unity's XR rendering data and shaders; there is no headset-specific
blur component or vendor SDK dependency in this module.

## Setup

1. Configure XR Plug-in Management and the XR provider appropriate to your headset
   (for example OpenXR). This repository does not install or select a provider.
2. Use a **World Space Canvas** for the panel. Assign the XR viewing camera to
   the canvas and **UIBlur → Source Camera**.
3. Choose **UIBlur → Source = Shared Backdrop**. If an active stereo camera is
   detected while Screen Capture is selected, UIBlur automatically uses the shared
   backend; it does not create a mono capture camera for the headset.
4. Add **UIBlurRendererFeature** to the Universal Renderer used by the XR camera.
   PC_Renderer and Mobile_Renderer already contain this feature. Do not add it twice.
5. Adjust shared blur quality on that renderer feature. The existing starting
   values are Downsample 4, Blur Size 1, Blur Iterations 1, HDR off. They are not a
   measured performance recommendation for every headset.
6. Use your normal XR UI input/ray setup for interaction. Blur rendering does not
   install an XR Origin, controller bindings or an input module.

The optional PhoneCameraBackground component is for the non-AR phone test only.
Do not add it to the VR camera. For AR Foundation, ARCameraBackground supplies the
background, and the same shared blur feature follows it.

## Rendering paths

- **Single-pass instanced / multiview:** inherit the camera's texture-array layout
  and eye count. URP's XR-aware blits process all slices. Panel and blur fragments
  sample the current eye's slice, so neither eye reuses the other eye's backdrop.
- **Multipass:** each eye's camera render produces and displays its own shared
  backdrop before the next eye render. The texture remains a regular 2D texture.
- **Mono / handheld AR / phone preview:** the regular 2D path remains available.

The pass runs after the background and opaque scene, before transparent UI.
One camera backdrop is shared by its panels. Extra panels add UI/fill cost; blur
work scales with eye resolution, number of views and refresh rate. Each eye uses
the existing filtered downsample and normalized Gaussian kernel.

Camera descriptor dimension, volumeDepth and vrUsage are preserved; blur targets
have no depth, mipmaps or MSAA. The existing Blitter/RenderGraph utilities are used,
with no legacy CommandBuffer.Blit or per-panel capture camera in the XR path.

Sprite slicing, decoration opacity, tint, rounded corners, borders and stencil
child masking remain on the same UIBlur panel. Legacy MeshBlur and the internal
UIBlurScreenCapture module are not separate VR rendering paths.

## Validation and limits

Editor GPU regression tests feed different colors into two array slices and
check that the downsample, horizontal/vertical blur, and panel shader preserve
both eyes independently. Descriptor tests cover array stereo and regular mono
targets. These tests do not simulate a headset, tracking, optical distortion or
the full XR compositor.

The regular camera preview was also checked after these changes. The backdrop
matched the pre-change reference; dynamic UI font atlas regeneration changed text
rasterization in the screenshot comparison.

Actual headset validation remains necessary, including:

- eye alignment while moving/rotating panels and the headset;
- sprite masks and sharp child UI in each eye;
- the selected XR loader and graphics API (especially mobile multiview);
- MSAA, render scale, dynamic resolution and foveated rendering settings;
- GPU frame time at the intended headset refresh rate.

The current project has no installed XR provider. No headset was exercised here.
The URP Universal Renderer is required; 2D Renderer and deprecated double-wide
stereo are outside this implementation. Non-uniform raster/foveated configurations
are not validated; start with those disabled when checking a new platform.

## Unity references

- [Single-pass instanced rendering and custom shaders](https://docs.unity.cn/Manual/SinglePassInstancing.html)
- [URP RenderGraph XR-aware blit parameters, Core 17.3](https://docs.unity.cn/Packages/com.unity.render-pipelines.core%4017.3/api/UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils.BlitMaterialParameters.html)

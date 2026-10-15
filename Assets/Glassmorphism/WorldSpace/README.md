# Glass on world space canvases and handheld AR

For a live phone camera preview without AR, use [PhoneCameraGlass.unity and its setup notes](PhoneCameraTest.md). That scene uses an Overlay canvas with a directly shared WebCamTexture blur.

`WorldSpaceGlass` is a separate uGUI component for phone/tablet AR and normal perspective cameras. It uses `WorldGlassRendererFeature` to blur the camera color once per viewing camera. All panels viewed by that camera share the result. There are no capture cameras, CPU camera-image conversions, or capture-time UI toggles.

The component supports both **World Space** and **Screen Space - Camera** canvases. For the latter, assign the viewing camera to the Canvas's Render Camera and the glass Source Camera. Screen Space Overlay is not supported. In either mode the backdrop includes the AR background and opaque geometry; it does not include other transparent UI. Use `GlassUIBlur` when you need to capture other screen space UI behind the panel.

## Sliced images

Set the sprite's Border values in Sprite Editor, then choose **Image Type → Sliced** on the glass component. **Pixels Per Unit Multiplier** scales the effective pixel density: 2 makes borders half as thick, 0.5 makes them twice as thick. This uses the sprite Pixels Per Unit and the canvas Reference Pixels Per Unit, like Unity's Image component. Border thickness is measured in local canvas units, so changing the world canvas scale changes its physical size normally. Slicing fills the panel rect; Preserve Image Aspect applies to Simple mode. Sprites without borders fall back to Simple. Decoration and mask use the same remapping, with no extra texture samples or render passes. The component's separate Border Width/Color adds a shader border and is not the sliced image border.

## Try it

1. Drag `WorldSpaceGlassCanvas.prefab` into your scene and position/rotate it in front of your camera. It is a world space canvas about 0.354 × 0.270 metres at its default scale. In AR, parent it to your placed content/anchor.
2. Assign the viewing **AR Camera** to the Canvas's **Event Camera** and the Glass Panel's **Source Camera**. The latter falls back to the canvas camera, then `Camera.main`.
3. Put buttons and text under **Glass Panel / Content**. Set up an EventSystem/input module if you want interaction.
4. Assign **Panel Image** on `WorldSpaceGlass`. One sprite supplies both the mask shape and decorative image. Use Image Color = white, alpha = 1; Tint alpha = 0; Border Width = 0; Corner Radius = 0 to preserve the sprite appearance. Keep RawImage Color white at full alpha. The sprite's own transparency remains intact for decoration, independently of the blur's silhouette.

The project's `PC_Renderer` and `Mobile_Renderer` already contain **World Glass Backdrop**. On another renderer, select its Universal Renderer Data asset and choose **Add Renderer Feature → World Glass Renderer Feature**. Keep the feature's Blur Shader and the component's Panel Shader assigned so both shaders are included in builds. Avoid adding this feature twice to the same renderer.

## AR Foundation setup

This repository does not install AR Foundation or device providers. In the AR project, use your normal AR Session / XR Origin with ARCameraManager and ARCameraBackground on its camera, plus the appropriate ARCore/ARKit provider. Enable **AR Background Renderer Feature** on the same renderer used by the AR camera, as required by [Unity's AR Foundation URP setup](https://docs.unity3d.com/Packages/com.unity.xr.arfoundation@6.0/manual/project-setup/universal-render-pipeline.html).

The blur runs at **BeforeRenderingTransparents**, after the AR background and opaque virtual geometry. It samples the already-oriented camera output, so it does not duplicate the platform's camera texture conversion or display transform. Keep custom background passes before this event. Transparent virtual objects, other world UI, and other glass panels are intentionally absent from the blurred backdrop; foreground content is rendered sharp afterward.

## Performance and rendering

- Blur settings belong to the renderer feature and are shared by all panels: start with **Downsample = 4**, **Blur Size = 1**, **Blur Iterations = 1**, **HDR Capture = off**.
- That uses one filtered downsample and two five-fetch Gaussian passes at one quarter of the camera width/height. The downsample covers the destination pixel footprint (four bilinear samples at 4×, sixteen at 8×) to avoid aliasing thin details. Radii above 1 use a denser normalized Gaussian kernel rather than stretching five samples apart; the maximum is 33 fetches per direction at radius 8. Pass count stays the same. More panels add UI draw/fill cost, rather than more camera captures or blur passes.
- URP needs a sampleable intermediate camera color buffer. This has a bandwidth/memory cost, even though the blur buffers are small and have no depth/MSAA/mipmaps.
- No capture is performed when that camera has no active world space glass components. Disable unused canvases/panels; active panels outside the view still request the shared blur.
- Perspective projection is calculated in the shader, so a rotated panel samples the correct screen positions instead of stretching a cropped image across its quad. Native depth testing controls occlusion by opaque virtual objects.
- The sprite overlay uses the same sRGB compositing as the screen space glass. Postprocessing and camera color grading can still change the final appearance.
- This implementation targets Unity 6 / URP 17 Universal Renderer and monoscopic handheld AR. Render Graph was exercised here. A compatibility-mode path is included but was not exercised in this Unity configuration. XR stereo/headset rendering and 2D Renderer are not supported.

## Verification

Editor checks rendered two tilted panels over a checkerboard: the panels shared one backdrop, the checkerboard blurred only inside their masks, and child graphics remained sharp. A gradient reference check matched the projected backdrop position with zero sampled RGB difference at the tested interior point. Both shaders compiled without errors, and rendering created no additional cameras. Live AR camera orientation, tracking, occlusion, and device GPU timing still require a phone/tablet build.

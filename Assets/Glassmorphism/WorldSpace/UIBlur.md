# UI Blur: one component, separate rendering modules

Add **Glassmorphism → UI Blur** to a uGUI panel. It requires a RawImage and Mask.
Use the same Panel Appearance controls for either source: sprite, slicing,
pixel multiplier, decoration color/opacity, tint, corners and border. Child UI
remains sharp and uses the same mask.

## Source selection

| Source | Background included | Canvas | Quality settings |
| --- | --- | --- | --- |
| Screen Capture | Scene and background UI rendered by the main camera | Screen Space Camera source canvas | UI Blur → Capture Quality |
| Shared Backdrop | Camera background and opaque scene geometry; excludes transparent UI | World Space or Screen Space Camera | UI Blur Renderer Feature |

Both sources need **UI Blur Renderer Feature** on the viewing camera’s URP renderer. Assign Source Camera, or use the canvas camera/Camera.main fallback.
For Shared Backdrop, quality settings remain on the renderer because every panel shares that blur.

The phone test follows the same pipeline as AR:
**camera background → opaque scene → shared renderer blur → glass and foreground UI**.

- Non-AR phone test: **PhoneCameraBackground** opens the camera.
  **PhoneCameraBackgroundRendererFeature** draws its sharp feed before opaque
  objects. The viewing camera uses Solid Color clearing.
- AR Foundation: **ARCameraBackground / AR Background Renderer Feature** supplies
  the camera background instead. The phone adapter is not used.
- Both then use **UIBlurRendererFeature** for all blur processing.

There is no phone-specific field or texture-provider bypass on UIBlur.
For headset rendering, use the [VR setup guide](VR.md). Stereo cameras automatically
use the shared renderer module, even if Screen Capture was selected.

## Screen Capture setup

1. Use a **Screen Space Camera** canvas with the viewing camera assigned.
2. Add **UI Blur Renderer Feature** to that camera's URP Universal Renderer.
3. In the feature Inspector, use **Configure Screen Capture UI Layer**. This reserves
   layer **31 / UIBlurForeground** and excludes it from the renderer's normal
   Transparent Layer Mask. Keep layer 31 included in the camera's Culling Mask.
   The supplied PC/Mobile renderers are already configured. An existing use of
   layer 31 must be moved before enabling Screen Capture.
4. Add UI Blur to the panel and choose Screen Capture. The module manages nested
   Canvas/GraphicRaycaster components and canvas layers; child Graphic layers and
   hierarchy remain intact. Disabling the module restores original canvas layers.

Rendering order is **scene + background UI → downsample/blur → glass + sharp child UI**,
all on the main camera, before postprocessing. Put sharp content under the panel.
Background UI across the camera renders before all capture panels, regardless of
its previous sibling position. Overlapping glass panels share the same background;
they do not recursively blur one another. Screen Space Overlay is unsupported.

Matching Downsample/Blur Size/Blur Iterations/HDR settings share one blur result.
At most **eight distinct quality groups per camera** are supported. This is a group
limit, not a panel count limit. Capture dimensions use camera native pixels divided
by Downsample even when URP render scale is below 1; source detail is still limited
by that render scale. No extra scene render or full-resolution capture copy is made.

Panel-owned stencil Masks, nested child Masks and ancestor **RectMask2D** clipping
are supported. A stencil **Mask above the foreground panel** is unsupported: its
stencil is cleared during the background stage. Move that mask into the foreground
panel hierarchy or use RectMask2D. The module warns about unsupported placement.

The old per-panel blur shader field is retained only for serialization. Capture
blur uses the shader on UI Blur Renderer Feature; panel appearance shaders remain
on UI Blur. VR continues to use Shared Backdrop automatically.

## Internal boundaries

- **UIBlur.cs**: public panel settings, source selection and module lifecycle.
- **Module 1 — UIBlurScreenCapture.cs**: native foreground Canvas staging, screen-region
  UV mapping and panel material. **UIBlurScreenCapturePass.cs** groups quality settings,
  downsamples/blurs the main camera and draws foreground UI. Both live in WorldSpace
  so the capture pass can use URP without adding URP dependencies to common helpers.
- **Module 2 — UIBlurSharedBackdrop.cs**: projected sampling, shared-camera registration,
  and panel material. Camera blur is produced by
  **UIBlurRendererFeature.cs**; an optional phone feed is produced by
  **PhoneCameraBackground.cs**.
- **UIBlurGaussianKernel / UIBlurImageSlicing**: common rendering helpers.

The two backend components remain independent. Script GUIDs and component file IDs
are retained; MovedFrom attributes record the previous serialized class names.
They are hidden behind UIBlur and removed from Add Component search. Only the
selected backend is enabled. Switching source disables/releases the old backend
before enabling the new one; unused module objects may remain disabled for reuse.
Disabling UIBlur disables both. Removing only UIBlur exposes the disabled backend
components so they can be removed or inspected.

UIBlur settings are authoritative. Runtime scripts/animations should now target
UIBlur rather than changing the hidden backend's appearance fields directly.

## Existing assets

UI.unity, PhoneCameraGlass.unity and WorldSpaceGlassCanvas.prefab have been migrated.
Existing appearance, capture quality and camera assignments are retained.
Old backend components are retained, not destroyed or replaced.

For additional old assets use **Tools → Glassmorphism → Migrate Panels to UI Blur**.
This is an explicit project migration that saves affected scenes and prefabs;
save unrelated scene work before running it. Repeating it skips panels that already
have UIBlur. No runtime asset migration or XR provider installation is performed.

## Script naming

- UIBlur: user-facing panel.
- UIBlurScreenCapture and UIBlurScreenCapturePass: main-camera UI capture backend.
- UIBlurSharedBackdrop: shared-backdrop backend.
- UIBlurRendererFeature: scene/AR/phone blur producer and main-camera UI capture scheduling.
- UIBlurGaussianKernel and UIBlurImageSlicing: common helpers.
- UIBlurEditor, UIBlurRendererFeatureEditor and UIBlurMigration: Editor-only Inspectors, renderer setup and old-panel conversion.
- PhoneCameraBackground and PhoneCameraBackgroundRendererFeature: optional phone source adapter.
- MeshBlur: separate legacy mesh-based glass; not part of the uGUI panel setup.

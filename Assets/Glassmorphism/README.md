# Glassmorphism — Frosted-Glass Panels for URP

For headset rendering with the shared blur, see [VR setup](WorldSpace/VR.md).

## Unified uGUI component

Use **Glassmorphism → UI Blur** for both rendering paths. See the [UIBlur guide](WorldSpace/UIBlur.md) for source selection, shared quality settings and the internal module boundaries. Existing sample scenes/prefabs have been migrated. The original component names below now describe internal compatibility modules; they are no longer separate Add Component entries.

For phone/tablet AR and perspective **world space canvases**, use UI Blur in Shared Backdrop mode with the [starter prefab](WorldSpace/README.md). It shares one camera-color blur between panels without secondary capture cameras.

`UIBlurScreenCapture` is the internal uGUI module used by `Assets/Scenes/UI.unity`.
The main camera renders the scene and background UI once. `UIBlurRendererFeature`
then downsamples and blurs that camera color, and draws the glass panels and sharp
child UI afterward using native UI batches. There are no secondary capture cameras,
full-resolution per-panel capture targets, or capture-time Graphic/culling toggles.
Panels with matching Downsample, Blur Size, Blur Iterations and HDR Capture share
one result; different settings retain separate results. Zero blur skips the
horizontal/vertical passes. Appearance/sprite mapping remains cached.

Both supplied URP renderers reserve **layer 31 / UIBlurForeground** and exclude it
from their normal transparent pass. The camera still includes this layer. The module
adds a nested Canvas and GraphicRaycaster to stage each panel's native UI without
reparenting its contents. For another renderer, add UI Blur Renderer Feature and
use its **Configure Screen Capture UI Layer** button. See the
[setup and limitations](WorldSpace/UIBlur.md#screen-capture-setup).

Quality settings, native-pixel blur dimensions, Gaussian filtering and image
compositing are retained. Six reference cases covering radii 0/1/2.5, downsampling
2/4, slicing, stencil masking, RectMask2D and sharp child UI matched exactly in five
cases; the widest sliced case differed by at most 1/255 per color channel.
GPU rendering tests cover first-frame output, dynamic children, material updates,
quality sharing and stereo regressions. Device CPU/GPU savings remain unmeasured.

Downsampling now filters the source pixel footprint instead of taking a single sample that can miss thin details. A reduction of 2 uses one bilinear fetch, 4 uses four, and 8 uses sixteen (actual counts follow the texture dimensions). This replaces the existing downsample pass; it does not add a pass. Small blur radii up to 1 retain the five-fetch filter. Wider radii use a normalized Gaussian kernel with adjacent pixels paired through bilinear filtering, up to 33 fetches per direction at radius 8. This avoids the broad bands produced by stretching five samples far apart. Kernel weights are computed on the CPU only when the radius or material changes. High downsampling still reduces detail and cannot provide full-resolution quality.

### uGUI panel appearance

**Image Type → Sliced** uses the sprite's Border values from Sprite Editor to preserve corners/edges as the panel resizes. **Pixels Per Unit Multiplier** adjusts border thickness: 1 uses the normal sprite/canvas pixel ratio, 2 halves the border thickness, and 0.5 doubles it. The same sliced UVs drive the decoration and sprite mask. Sliced images fill the panel rect; Preserve Image Aspect applies to Simple mode. A sprite with no borders falls back to Simple. This adds no texture samples or rendering passes. The extra shader Border Width/Color controls remain separate from the sprite's own sliced border.

Assign a sprite to **Panel Image** on `UIBlur`. Its alpha supplies the glass shape and clips child content through the `Mask` on the same GameObject. **Image Color** controls the decorative overlay color and opacity independently of that mask. **Preserve Image Aspect** fits the sprite inside the panel; turn it off to stretch the shape to the panel rect. Tint, corner radius and border are also composited in this single glass graphic.

Place buttons and text beneath the glass GameObject. No separate mask Image or decorative child Image is needed. `UI.unity` has been migrated to this setup; the `Content` child only holds the foreground button. The combined shader is [Shaders/GlassUIPanel.shader](Shaders/GlassUIPanel.shader). Keep serialized panel shader references on the component and the blur shader reference on UI Blur Renderer Feature so they are included in builds.

The sprite mask uses an alpha cutoff of 0.001 with a smoothed boundary. A translucent sprite fill therefore keeps the blurred backdrop visible at full opacity; the original sprite alpha still controls the decoration.

The decorative image blends over the backdrop in sRGB display space, then converts back to linear in Linear projects. This matches sRGB image compositing instead of brightening translucent fills through linear blending. Image Color is a display-space multiplier; white at full alpha preserves the source image. This adds color conversion math without extra texture samples or rendering passes. At zero image opacity, the backdrop color is left unchanged.

This removes two UI graphics from the sample hierarchy. The blur passes and UI fill remain rendering costs; adding the panel sprite does not add a capture or blur pass.

The world-space `MeshBlur` component is described below.

No Nova, no uGUI, no third-party dependencies. Drop a `MeshBlur` component onto a GameObject and you get a world-space frosted-glass quad with rounded corners, tint, and an edge highlight — all driven by a real per-panel backdrop blur.

## Legacy MeshBlur: how it works

For each `MeshBlur`:

1. A hidden secondary camera mirrors the gameplay camera each frame.
2. Its **projection matrix** is rebuilt as an off-axis frustum that frames only the panel's on-screen rect — so the captured texture is exactly what's behind that panel.
3. The capture is rendered into a downsampled `RenderTexture`.
4. A separable Gaussian (two passes per iteration) blurs it: [Shaders/SeparableBlur.shader](Shaders/SeparableBlur.shader).
5. The blurred RT is sampled by [Shaders/GlassPanel.shader](Shaders/GlassPanel.shader), which applies a rounded-rect SDF for the body alpha and a thin inner ring for the border.

A configurable `Exclude Layers` mask keeps the panels themselves out of the capture.

## Legacy MeshBlur setup

1. Create an empty GameObject. Add the `Glassmorphism/Glass Panel` component (auto-adds `MeshFilter` + `MeshRenderer`).
2. Scale the transform — `lossyScale.x` becomes the panel width, `lossyScale.y` the height, in world units.
3. Set:
   - `Source Camera` — your gameplay camera (defaults to `Camera.main`).
   - `Exclude Layers` — a layer you assign all glass panels to, so they don't blur themselves.
4. Tweak `Tint` (alpha controls how strongly the glass tints the backdrop), `Corner Radius`, `Border Width`, `Border Color`.
5. Tune `Downsample` / `Blur Size` / `Blur Iterations` for the frost level vs. cost.

That's it — no Canvas, no Nova, no UI framework involvement.

## Legacy MeshBlur cost

One off-screen camera render + `2 × blurIterations` blit passes per panel per frame. Cheap for a handful of panels. For many panels, switch to a single screen-blur RT and have each panel sample its screen-space sub-rect (not included).

## Files

- [Shaders/SeparableBlur.shader](Shaders/SeparableBlur.shader) — two-pass (`BlurH`, `BlurV`) Gaussian approximation, 5 samples per pass.
- [Shaders/GlassPanel.shader](Shaders/GlassPanel.shader) — rounded-rect SDF + tint + border, samples the blurred backdrop.
- [Runtime/MeshBlur.cs](Runtime/MeshBlur.cs) — the only component you attach.
- [WorldSpace/UIBlurScreenCapture.cs](WorldSpace/UIBlurScreenCapture.cs) — uGUI component used by `Assets/Scenes/UI.unity`.
- [Runtime/Glassmorphism.asmdef](Runtime/Glassmorphism.asmdef) — no external references.

## Legacy MeshBlur caveats

- World-space only. For a Screen-Space-Overlay Canvas, you'd need a single full-screen blur RT and a custom uGUI `Graphic` that samples it in screen-UV space — not included.
- The off-axis frustum assumes the panel is roughly view-aligned. Tilted panels still work (you'll see what's behind their screen-space bounding rect, blurred and stretched onto the panel), but if you want a "real" tilted-glass refraction you'd render a per-panel camera looking through the panel's normal — out of scope.
- Built for URP 17.x. The blur shader uses `Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl`; on Built-in RP, swap the include for `UnityCG.cginc` and the macros to the Built-in equivalents.

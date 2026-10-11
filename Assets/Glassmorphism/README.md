# Glassmorphism — Frosted-Glass Panels for URP

`GlassUIBlur` is the uGUI blur used by `Assets/Scenes/UI.unity`. It captures the Screen Space Camera canvas at the source camera's full pixel dimensions so the canvas does not change scale, then downsamples once and applies separable Gaussian blur. The panel displays its screen region through `RawImage.uvRect`. The scene uses `Downsample = 2`, `Blur Size = 1`, and `Blur Iterations = 2`. `HDR Capture` is off by default to reduce render target bandwidth. Enable it only if the glass must preserve HDR highlights.

This UI path still renders an extra camera each frame. The cost scales with the capture resolution and scene complexity. Profile the blur camera in Unity's Frame Debugger or GPU Profiler on the target device before adding many blur panels.

### uGUI panel appearance

Assign a sprite to **Panel Image** on `GlassUIBlur`. Its alpha supplies the glass shape and clips child content through the `Mask` on the same GameObject. **Image Color** controls the decorative overlay color and opacity independently of that mask. **Preserve Image Aspect** fits the sprite inside the panel; turn it off to stretch the shape to the panel rect. Tint, corner radius and border are also composited in this single glass graphic.

Place buttons and text beneath the glass GameObject. No separate mask Image or decorative child Image is needed. `UI.unity` has been migrated to this setup; the `Content` child only holds the foreground button. The combined shader is [Shaders/GlassUIPanel.shader](Shaders/GlassUIPanel.shader). Keep a serialized reference to both shaders on the component so they are included in builds.

The sprite mask uses an alpha cutoff of 0.001 with a smoothed boundary. A translucent sprite fill therefore keeps the blurred backdrop visible at full opacity; the original sprite alpha still controls the decoration.

The decorative image blends over the backdrop in sRGB display space, then converts back to linear in Linear projects. This matches sRGB image compositing instead of brightening translucent fills through linear blending. Image Color is a display-space multiplier; white at full alpha preserves the source image. This adds color conversion math without extra texture samples or rendering passes. At zero image opacity, the backdrop color is left unchanged.

This removes two UI graphics from the sample hierarchy. The live camera capture and blur passes are still the main rendering cost; adding the panel sprite does not add a capture or blur pass.

The world-space `GlassPanel` component is described below.

No Nova, no uGUI, no third-party dependencies. Drop a `GlassPanel` component onto a GameObject and you get a world-space frosted-glass quad with rounded corners, tint, and an edge highlight — all driven by a real per-panel backdrop blur.

## How it works

For each `GlassPanel`:

1. A hidden secondary camera mirrors the gameplay camera each frame.
2. Its **projection matrix** is rebuilt as an off-axis frustum that frames only the panel's on-screen rect — so the captured texture is exactly what's behind that panel.
3. The capture is rendered into a downsampled `RenderTexture`.
4. A separable Gaussian (two passes per iteration) blurs it: [Shaders/SeparableBlur.shader](Shaders/SeparableBlur.shader).
5. The blurred RT is sampled by [Shaders/GlassPanel.shader](Shaders/GlassPanel.shader), which applies a rounded-rect SDF for the body alpha and a thin inner ring for the border.

A configurable `Exclude Layers` mask keeps the panels themselves out of the capture.

## Setup

1. Create an empty GameObject. Add the `Glassmorphism/Glass Panel` component (auto-adds `MeshFilter` + `MeshRenderer`).
2. Scale the transform — `lossyScale.x` becomes the panel width, `lossyScale.y` the height, in world units.
3. Set:
   - `Source Camera` — your gameplay camera (defaults to `Camera.main`).
   - `Exclude Layers` — a layer you assign all glass panels to, so they don't blur themselves.
4. Tweak `Tint` (alpha controls how strongly the glass tints the backdrop), `Corner Radius`, `Border Width`, `Border Color`.
5. Tune `Downsample` / `Blur Size` / `Blur Iterations` for the frost level vs. cost.

That's it — no Canvas, no Nova, no UI framework involvement.

## Cost

One off-screen camera render + `2 × blurIterations` blit passes per panel per frame. Cheap for a handful of panels. For many panels, switch to a single screen-blur RT and have each panel sample its screen-space sub-rect (not included).

## Files

- [Shaders/SeparableBlur.shader](Shaders/SeparableBlur.shader) — two-pass (`BlurH`, `BlurV`) Gaussian approximation, 5 samples per pass.
- [Shaders/GlassPanel.shader](Shaders/GlassPanel.shader) — rounded-rect SDF + tint + border, samples the blurred backdrop.
- [Runtime/GlassPanel.cs](Runtime/GlassPanel.cs) — the only component you attach.
- [Runtime/GlassUIBlur.cs](Runtime/GlassUIBlur.cs) — uGUI component used by `Assets/Scenes/UI.unity`.
- [Runtime/Glassmorphism.asmdef](Runtime/Glassmorphism.asmdef) — no external references.

## Caveats

- World-space only. For a Screen-Space-Overlay Canvas, you'd need a single full-screen blur RT and a custom uGUI `Graphic` that samples it in screen-UV space — not included.
- The off-axis frustum assumes the panel is roughly view-aligned. Tilted panels still work (you'll see what's behind their screen-space bounding rect, blurred and stretched onto the panel), but if you want a "real" tilted-glass refraction you'd render a per-panel camera looking through the panel's normal — out of scope.
- Built for URP 17.x. The blur shader uses `Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl`; on Built-in RP, swap the include for `UnityCG.cginc` and the macros to the Built-in equivalents.

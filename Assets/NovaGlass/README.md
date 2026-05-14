# NovaGlass — Glassmorphism for Nova UIBlock2D (URP)

A drop-in frosted-glass / glassmorphism backdrop for [Nova UI](https://novaui.io) blocks. Nova has no native backdrop-blur, so this fills the gap: it captures the world behind each panel, blurs it, and hands the result to a `UIBlock2D` as its image. Nova's body color, border, shadow and rounded corners then style the glass on top.

## How it works

For each `NovaGlassBackdrop` component:

1. A hidden secondary camera mirrors the main camera's pose and clip planes.
2. Its **projection matrix** is rebuilt every `LateUpdate` as an **off-axis frustum** that frames only the panel's on-screen rect — so the captured texture is exactly what's behind that panel.
3. The capture is rendered into a downsampled `RenderTexture`.
4. A separable Gaussian (two passes per iteration) blurs it: `Assets/NovaGlass/Shaders/NovaGlassBlur.shader`.
5. The blurred RT is assigned via `UIBlock2D.SetImage(rt)`. Nova samples it with its normal UVs, so the blur lines up 1:1 with the panel.

The Nova UI layer is excluded from the capture (via `excludeLayers`) so panels don't blur themselves or each other.

## Setup

1. Create a Unity layer for your Nova UI (e.g. `NovaUI`) and assign your panels to it.
2. Add `NovaGlassBackdrop` to a GameObject that already has a `UIBlock2D`.
3. On the component:
   - `sourceCamera` — your gameplay camera (defaults to `Camera.main`).
   - `excludeLayers` — tick the `NovaUI` layer.
   - `downsample` — 4 is a good start. Higher = cheaper, softer.
   - `blurSize` / `blurIterations` — how frosty.
4. On the `UIBlock2D`:
   - Set `Color` to something like `(1, 1, 1, 0.25)` — this is the **glass tint**.
   - Give it a `CornerRadius` for the typical rounded-glass look.
   - Optional: thin white `Border` at low alpha for the "edge highlight".
   - Optional: soft `Shadow` for depth.

## Cost

One off-screen camera render + `2 × blurIterations` blit passes per panel per frame. Two or three glass panels are fine. If you need a dozen, switch to a single shared screen-blur RT and a custom UV mapping per panel (not included).

## Files

- `Shaders/NovaGlassBlur.shader` — separable Gaussian, two passes (`BlurH`, `BlurV`).
- `Runtime/NovaGlassBackdrop.cs` — the component you attach.
- `Runtime/NovaGlass.asmdef` — assembly definition, references `Nova`.

## Caveats

- The off-axis frustum only works for cameras whose forward axis the panel faces. World-space panels at arbitrary angles will still get a captured rect, but the perspective will be the source camera's, not the panel's normal. For a perfectly view-aligned reflection of what's "behind" a tilted panel you'd want a per-panel camera looking through it — out of scope here.
- HDR + post-processing on the source camera is preserved (it's a `Camera.CopyFrom`). If post is heavy, disable HDR on the blur camera by editing `ConfigureBlurCamera`.
- In edit mode the component runs every frame (`[ExecuteAlways]`) so you can preview in the Scene view. If that's noisy, gate `LateUpdate` on `Application.isPlaying`.

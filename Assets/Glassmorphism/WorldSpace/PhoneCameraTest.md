# Phone camera glass test (no AR)

Open `Assets/Scenes/PhoneCameraGlass.unity`. It is the first enabled scene in Build Settings.

1. Select Android or iOS in Build Profiles and choose your connected device.
2. Build and Run. On iOS, complete the normal signing setup in Xcode.
3. Allow camera access. The scene prefers the rear camera and requests 1280×720 at 30 FPS; actual device modes can differ.

The scene has a fullscreen Screen Space Overlay canvas, a camera preview and a glass panel. `PhoneCameraBackdrop` displays the WebCamTexture and blurs it once for all linked panels. The glass's **Camera Backdrop** reference bypasses the URP camera capture feature, so AR Foundation is not needed. There are no CPU pixel readbacks or extra capture cameras. Aspect fill, camera rotation, vertical flip and optional front camera mirroring use the same UV mapping for preview and blur.

The Editor shows a labelled static preview image. Play mode opens a desktop webcam if available; a phone build uses the phone camera. The static image can remain visible while permission/startup is pending, or if the camera fails; the status message identifies this. **Start Camera On Play** must remain enabled for a live test (it is enabled in the saved scene).

Adjust **Downsample**, **Blur Size** and **Blur Iterations** on **Live Camera Preview → Phone Camera Backdrop**. The default is 4×, radius 1, one iteration. All linked panels reuse the same two small blur buffers. The preview itself stays sharp. The source is the camera image, not other UI drawn above it.

iOS Camera Usage Description is configured. Android permission is requested before opening the camera. No microphone access is requested. Camera hardware and permission prompts still need verification on the actual device; Editor checks exercised Overlay rendering, matching preview/blur alignment, and UV transforms for all four rotations.

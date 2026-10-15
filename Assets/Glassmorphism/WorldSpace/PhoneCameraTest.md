# Phone camera test using the shared renderer blur

Open `Assets/Scenes/PhoneCameraGlass.unity`. It is the first enabled build scene.

1. Build and Run on Android or iOS; complete the usual iOS signing setup.
2. Allow camera access. The rear camera requests 1280×720 at 30 FPS; the actual
   device mode can differ.
3. Adjust blur quality on **UIBlurRendererFeature** in the URP renderer asset,
   rather than on the phone source or individual panels.

## Architecture

**Phone camera → camera background pass → opaque scene → shared blur → glass/UI**

- **Main Camera / PhoneCameraBackground** manages camera permission, startup,
  pause/resume, aspect fill, rotation and mirroring. It owns no blur textures.
- **PhoneCameraBackgroundRendererFeature** draws the sharp feed directly into the
  viewing camera's color buffer at BeforeRenderingOpaques. The camera must use
  Solid Color clearing, rather than a skybox that would overwrite this background.
- **UIBlurRendererFeature** blurs camera color once at BeforeRenderingTransparents.
  It includes the real camera background and opaque virtual objects. It excludes
  transparent UI so foreground labels/buttons remain sharp.
- **Camera Canvas** is Screen Space Camera, assigned to Main Camera. The panel
  uses **UI Blur → Shared Backdrop** with that camera. There is no phone-specific
  reference on the panel.

Both PC_Renderer and Mobile_Renderer have the background adapter and shared blur
feature configured. The background adapter does nothing for cameras without an
active PhoneCameraBackground source. The previous Overlay preview object is
retained inactive in the test scene; it does not render or open another camera.

The Editor uses a labelled static preview. Start Camera On Play remains enabled
in the saved scene. Play mode can open a desktop webcam; a phone build opens its
camera. A static preview/status message remains available while startup or
permission is pending. iOS usage text is configured and Android requests camera
permission. No microphone or AR Foundation dependency is required.

## Moving to AR Foundation

Remove/disable PhoneCameraBackground and use the normal AR camera setup:
ARCameraManager, ARCameraBackground and the appropriate AR Foundation URP
background renderer feature. Keep **UIBlurRendererFeature** and the panel setup.
The optional phone background feature becomes inactive without a source.

This mirrors the rendering responsibilities of AR; it does not install AR
Foundation or provide tracking. Live device permission, camera orientation and
GPU performance still need an Android/iOS build. VR is not included.

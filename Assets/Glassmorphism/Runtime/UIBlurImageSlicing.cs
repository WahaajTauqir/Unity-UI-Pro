using UnityEngine;

namespace Glassmorphism
{
    public enum UIBlurImageType { Simple, Sliced }

    public static class UIBlurImageSlicing
    {
        static readonly int Enabled = Shader.PropertyToID("_ImageSliced");
        static readonly int Destination = Shader.PropertyToID("_SliceBorder");
        static readonly int Source = Shader.PropertyToID("_SliceUVBorder");

        public static bool IsSliced(Sprite sprite, UIBlurImageType type)
            => type == UIBlurImageType.Sliced && sprite != null && sprite.border.sqrMagnitude > 0;

        public static void Apply(Material material, Sprite sprite, UIBlurImageType type,
            Rect rect, Canvas canvas, float multiplier)
        {
            bool sliced = IsSliced(sprite, type);
            material.SetFloat(Enabled, sliced ? 1 : 0);
            if (!sliced) return;

            // Match uGUI Image's multipliedPixelsPerUnit in local canvas units.
            float referencePPU = canvas != null ? canvas.referencePixelsPerUnit : 100;
            float pixelsPerUnit = sprite.pixelsPerUnit / Mathf.Max(0.0001f, referencePPU)
                * Mathf.Max(0.01f, multiplier);
            Vector4 border = sprite.border / Mathf.Max(0.0001f, pixelsPerUnit);
            float width = Mathf.Max(0.0001f, rect.width);
            float height = Mathf.Max(0.0001f, rect.height);
            float horizontal = border.x + border.z;
            float vertical = border.y + border.w;
            // Small panels shrink opposing borders proportionally, as uGUI does.
            if (horizontal > width) { border.x *= width / horizontal; border.z *= width / horizontal; }
            if (vertical > height) { border.y *= height / vertical; border.w *= height / vertical; }
            material.SetVector(Destination, new Vector4(border.x / width, border.y / height,
                border.z / width, border.w / height));

            Vector4 outer = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);
            Vector4 inner = UnityEngine.Sprites.DataUtility.GetInnerUV(sprite);
            float uvWidth = Mathf.Max(0.0001f, outer.z - outer.x);
            float uvHeight = Mathf.Max(0.0001f, outer.w - outer.y);
            material.SetVector(Source, new Vector4(
                Mathf.Clamp01((inner.x - outer.x) / uvWidth),
                Mathf.Clamp01((inner.y - outer.y) / uvHeight),
                Mathf.Clamp01((outer.z - inner.z) / uvWidth),
                Mathf.Clamp01((outer.w - inner.w) / uvHeight)));
        }
    }
}

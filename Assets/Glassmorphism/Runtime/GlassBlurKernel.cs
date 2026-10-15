using UnityEngine;

namespace Glassmorphism
{
    /// Builds a normalized separable kernel only when the radius changes.
    /// Adjacent source pixels share one bilinear fetch, without leaving sampling gaps.
    public sealed class GlassBlurKernel
    {
        static readonly int Enabled = Shader.PropertyToID("_BlurKernelEnabled");
        static readonly int Size = Shader.PropertyToID("_BlurSize");
        static readonly int Center = Shader.PropertyToID("_BlurCenterWeight");
        static readonly int PairCount = Shader.PropertyToID("_BlurPairCount");
        static readonly int Kernel = Shader.PropertyToID("_BlurKernel");
        readonly Vector4[] pairs = new Vector4[16];
        float previousRadius = float.NaN;
        int pairCount;
        float centerWeight;

        public void Apply(Material material, float radius)
        {
            radius = Mathf.Clamp(radius, 0, 8);
            if (radius != previousRadius)
            {
                previousRadius = radius;
                Build(radius);
            }

            // Shader reloads can clear these nonserialized uniforms. Cache the
            // calculation, but upload the complete kernel before every render.
            material.SetFloat(Enabled, 1);
            material.SetFloat(Size, radius);
            material.SetFloat(Center, centerWeight);
            material.SetInt(PairCount, pairCount);
            material.SetVectorArray(Kernel, pairs);
        }

        void Build(float radius)
        {

            int count = 0;
            float center = 1;
            if (radius > 0 && radius <= 1)
            {
                // Preserve the existing inexpensive kernel for small blur radii.
                center = 0.227027f;
                pairs[0] = new Vector4(1.3846153f * radius, 0.3162162f, 0, 0);
                pairs[1] = new Vector4(3.230767f * radius, 0.0702703f, 0, 0);
                count = 2;
            }
            else if (radius > 1)
            {
                int support = Mathf.Min(32, Mathf.CeilToInt(4 * radius));
                float sigma = 1.69f * radius;
                float exponent = -0.5f / (sigma * sigma);
                float total = 1;
                for (int pixel = 1; pixel <= support; pixel += 2)
                {
                    float first = Mathf.Exp(pixel * pixel * exponent);
                    float second = pixel + 1 <= support ? Mathf.Exp((pixel + 1) * (pixel + 1) * exponent) : 0;
                    float weight = first + second;
                    float offset = (pixel * first + (pixel + 1) * second) / weight;
                    pairs[count++] = new Vector4(offset, weight, 0, 0);
                    total += 2 * weight;
                }
                center = 1 / total;
                for (int i = 0; i < count; i++) pairs[i].y /= total;
            }

            centerWeight = center;
            pairCount = count;
        }
    }
}

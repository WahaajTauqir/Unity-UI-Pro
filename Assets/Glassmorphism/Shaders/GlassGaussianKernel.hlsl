// Define GLASS_SAMPLE(uv) before including this file.
float _BlurKernelEnabled;
float _BlurCenterWeight;
int _BlurPairCount;
float4 _BlurKernel[16];

float4 GlassGaussianBlur(float2 uv, float2 texelDirection)
{
    float4 color = GLASS_SAMPLE(uv) * _BlurCenterWeight;
    [loop] for (int pair = 0; pair < _BlurPairCount; pair++)
    {
        float2 offset = texelDirection * _BlurKernel[pair].x;
        color += (GLASS_SAMPLE(uv + offset) + GLASS_SAMPLE(uv - offset)) * _BlurKernel[pair].y;
    }
    return color;
}

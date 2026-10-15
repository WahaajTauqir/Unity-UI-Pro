Shader "Glassmorphism/WorldGlassBlur"
{
    Properties
    {
        _BlurSize ("Blur Size", Float) = 1
        [HideInInspector] _DownsampleTargetSize ("Target Pixel Size", Vector) = (1,1,0,0)
        [HideInInspector] _BlurKernelEnabled ("Dense Kernel", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        HLSLINCLUDE
        #pragma target 3.5
        #pragma multi_compile_local_fragment _ UIBLUR_PRESERVE_ALPHA
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        float _BlurSize;
        float4 _DownsampleTargetSize;
        half4 SampleBackdrop(float2 uv)
        {
            // Clamp to the source's actual used viewport, including dynamic scale.
            float2 edge = abs(_BlitTexture_TexelSize.xy) * 0.5;
            float2 low = min(_BlitScaleBias.zw, _BlitScaleBias.zw + _BlitScaleBias.xy) + edge;
            float2 high = max(_BlitScaleBias.zw, _BlitScaleBias.zw + _BlitScaleBias.xy) - edge;
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, clamp(uv, low, high), 0);
        }
        #define GLASS_SAMPLE(uv) SampleBackdrop(uv)
        #include "../Shaders/GlassGaussianKernel.hlsl"
        half4 OutputColor(half4 color)
        {
            #if defined(UIBLUR_PRESERVE_ALPHA)
                return color;
            #else
                return half4(color.rgb, 1);
            #endif
        }
        half4 Gaussian(float2 uv, float2 direction)
        {
            if (_BlurKernelEnabled > 0.5)
                return OutputColor(GlassGaussianBlur(uv, abs(_BlitTexture_TexelSize.xy) * direction));
            float2 stepUV = _BlitTexture_TexelSize.xy * direction * _BlurSize;
            half4 color = SampleBackdrop(uv) * 0.227027;
            color += SampleBackdrop(uv + stepUV * 1.3846153) * 0.3162162;
            color += SampleBackdrop(uv - stepUV * 1.3846153) * 0.3162162;
            color += SampleBackdrop(uv + stepUV * 3.230767) * 0.07027;
            color += SampleBackdrop(uv - stepUV * 3.230767) * 0.07027;
            return OutputColor(color);
        }
        half4 Copy(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 footprint = _BlitScaleBias.xy * _DownsampleTargetSize.xy;
            float2 count = clamp(ceil(abs(footprint / _BlitTexture_TexelSize.xy) * 0.5), 1, 4);
            float4 sum = 0;
            [unroll] for (int y = 0; y < 4; y++)
            {
                [unroll] for (int x = 0; x < 4; x++)
                {
                    if (x < count.x && y < count.y)
                    {
                        float2 offset = ((float2(x, y) + 0.5) / count - 0.5) * footprint;
                        sum += SampleBackdrop(input.texcoord + offset);
                    }
                }
            }
            return OutputColor(sum / (count.x * count.y));
        }
        half4 Horizontal(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return Gaussian(input.texcoord, float2(1, 0));
        }
        half4 Vertical(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return Gaussian(input.texcoord, float2(0, 1));
        }
        ENDHLSL
        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Copy
            ENDHLSL
        }
        Pass
        {
            Name "Horizontal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Horizontal
            ENDHLSL
        }
        Pass
        {
            Name "Vertical"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Vertical
            ENDHLSL
        }
    }
}

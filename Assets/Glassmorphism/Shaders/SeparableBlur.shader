Shader "Glassmorphism/SeparableBlur"
{
    Properties
    {
        _MainTex  ("Source", 2D) = "white" {}
        _BlurSize ("Blur Size (texels)", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always Cull Off ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        float4 _MainTex_TexelSize;
        float  _BlurSize;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings Vert(Attributes IN)
        {
            Varyings OUT;
            OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
            OUT.uv         = IN.uv;
            return OUT;
        }

        static const float kWeights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

        float4 GaussianBlur(float2 uv, float2 dir)
        {
            float2 stepUV = _MainTex_TexelSize.xy * dir * _BlurSize;
            float4 col    = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * kWeights[0];

            UNITY_UNROLL
            for (int i = 1; i < 5; ++i)
            {
                col += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + stepUV * i) * kWeights[i];
                col += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - stepUV * i) * kWeights[i];
            }
            return col;
        }

        float4 FragHorizontal(Varyings IN) : SV_Target { return GaussianBlur(IN.uv, float2(1, 0)); }
        float4 FragVertical  (Varyings IN) : SV_Target { return GaussianBlur(IN.uv, float2(0, 1)); }
        ENDHLSL

        Pass
        {
            Name "BlurH"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment FragHorizontal
            ENDHLSL
        }

        Pass
        {
            Name "BlurV"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment FragVertical
            ENDHLSL
        }
    }
}

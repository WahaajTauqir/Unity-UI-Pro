Shader "Glassmorphism/GlassPanel"
{
    Properties
    {
        _MainTex      ("Blurred Backdrop", 2D) = "white" {}
        _TintColor    ("Tint", Color) = (1, 1, 1, 0.25)
        _BorderColor  ("Border Color", Color) = (1, 1, 1, 0.5)
        _BorderWidth  ("Border Width (world)", Float) = 0.01
        _CornerRadius ("Corner Radius (world)", Float) = 0.1
        _Size         ("Panel World Size (xy)", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "Queue"        = "Transparent"
            "RenderType"   = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            Name "GlassPanel"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
                float4 _BorderColor;
                float  _BorderWidth;
                float  _CornerRadius;
                float4 _Size;
            CBUFFER_END

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

            float RoundedRectSDF(float2 p, float2 halfSize, float r)
            {
                float2 q = abs(p) - (halfSize - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                float2 size     = _Size.xy;
                float2 halfSize = size * 0.5;
                float2 p        = (IN.uv - 0.5) * size;
                float  r        = min(_CornerRadius, min(halfSize.x, halfSize.y));
                float  d        = RoundedRectSDF(p, halfSize, r);

                float aa = max(fwidth(d), 1e-5);

                float bodyMask   = 1.0 - smoothstep(-aa, 0.0, d);
                float innerMask  = 1.0 - smoothstep(-aa, 0.0, d + _BorderWidth);
                float borderRing = saturate(bodyMask - innerMask);

                float3 bg     = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).rgb;
                float3 tinted = lerp(bg, _TintColor.rgb, _TintColor.a);
                float3 color  = lerp(tinted, _BorderColor.rgb, borderRing * _BorderColor.a);

                return float4(color, bodyMask);
            }
            ENDHLSL
        }
    }
}

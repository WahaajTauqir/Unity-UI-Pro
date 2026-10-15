Shader "Glassmorphism/SeparableBlur"
{
    Properties
    {
        _MainTex  ("Source", 2D) = "white" {}
        _BlurSize ("Blur Size (texels)", Float) = 2
        [HideInInspector] _DownsampleFootprint ("Downsample Footprint", Vector) = (1,1,0,0)
        [HideInInspector] _BlurKernelEnabled ("Dense Kernel", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZTest Always Cull Off ZWrite Off

        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        float _BlurSize;
        float4 _DownsampleFootprint;
        #define GLASS_SAMPLE(uv) tex2D(_MainTex, uv)
        #include "GlassGaussianKernel.hlsl"

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        v2f Vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        fixed4 GaussianBlur(float2 uv, float2 dir)
        {
            if (_BlurKernelEnabled > 0.5)
                return GlassGaussianBlur(uv, abs(_MainTex_TexelSize.xy) * dir);
            float2 stepUV = _MainTex_TexelSize.xy * dir * _BlurSize;
            // Bilinear filtering approximates the nine-tap kernel with five fetches.
            fixed4 col = tex2D(_MainTex, uv) * 0.227027;
            col += tex2D(_MainTex, uv + stepUV * 1.3846153) * 0.3162162;
            col += tex2D(_MainTex, uv - stepUV * 1.3846153) * 0.3162162;
            col += tex2D(_MainTex, uv + stepUV * 3.2307670) * 0.07027;
            col += tex2D(_MainTex, uv - stepUV * 3.2307670) * 0.07027;
            return col;
        }

        fixed4 FragHorizontal(v2f i) : SV_Target { return GaussianBlur(i.uv, float2(1, 0)); }
        fixed4 FragVertical  (v2f i) : SV_Target { return GaussianBlur(i.uv, float2(0, 1)); }

        fixed4 FragDownsample(v2f i) : SV_Target
        {
            // Each bilinear fetch covers up to 2x2 source pixels. Use a larger
            // footprint for 3x-8x reduction instead of skipping source details.
            float2 count = clamp(ceil(_DownsampleFootprint.xy * 0.5), 1, 4);
            float2 footprint = abs(_MainTex_TexelSize.xy) * _DownsampleFootprint.xy;
            float4 sum = 0;
            [unroll] for (int y = 0; y < 4; y++)
            {
                [unroll] for (int x = 0; x < 4; x++)
                {
                    if (x < count.x && y < count.y)
                    {
                        float2 offset = ((float2(x, y) + 0.5) / count - 0.5) * footprint;
                        sum += tex2D(_MainTex, i.uv + offset);
                    }
                }
            }
            return sum / (count.x * count.y);
        }
        ENDCG

        Pass
        {
            Name "BlurH"
            CGPROGRAM
            #pragma vertex   Vert
            #pragma fragment FragHorizontal
            ENDCG
        }

        Pass
        {
            Name "BlurV"
            CGPROGRAM
            #pragma vertex   Vert
            #pragma fragment FragVertical
            ENDCG
        }

        Pass
        {
            Name "FilteredDownsample"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDownsample
            ENDCG
        }
    }

    Fallback Off
}

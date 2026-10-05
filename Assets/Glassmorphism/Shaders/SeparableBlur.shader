Shader "Glassmorphism/SeparableBlur"
{
    Properties
    {
        _MainTex  ("Source", 2D) = "white" {}
        _BlurSize ("Blur Size (texels)", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZTest Always Cull Off ZWrite Off

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        float _BlurSize;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        v2f Vert(appdata_img v)
        {
            v2f o;
            // Graphics.Blit mesh is already in clip space.
            o.pos = float4(v.vertex.xy, 0.0, 1.0);
            o.uv = v.texcoord;
            return o;
        }

        static const float kWeights[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

        fixed4 GaussianBlur(float2 uv, float2 dir)
        {
            float2 stepUV = _MainTex_TexelSize.xy * dir * _BlurSize;
            fixed4 col = tex2D(_MainTex, uv) * kWeights[0];

            for (int i = 1; i < 5; ++i)
            {
                col += tex2D(_MainTex, uv + stepUV * i) * kWeights[i];
                col += tex2D(_MainTex, uv - stepUV * i) * kWeights[i];
            }
            return col;
        }

        fixed4 FragHorizontal(v2f i) : SV_Target { return GaussianBlur(i.uv, float2(1, 0)); }
        fixed4 FragVertical  (v2f i) : SV_Target { return GaussianBlur(i.uv, float2(0, 1)); }
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
    }

    Fallback Off
}

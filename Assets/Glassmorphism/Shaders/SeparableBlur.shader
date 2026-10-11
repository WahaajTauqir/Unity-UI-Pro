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
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        fixed4 GaussianBlur(float2 uv, float2 dir)
        {
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

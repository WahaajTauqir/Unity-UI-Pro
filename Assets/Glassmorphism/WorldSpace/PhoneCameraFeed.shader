Shader "Glassmorphism/PhoneCameraFeed"
{
    Properties
    {
        [PerRendererData] _MainTex ("Camera Feed", 2D) = "black" {}
        _CameraUV0 ("Camera UV Row 0", Vector) = (1,0,0,0)
        _CameraUV1 ("Camera UV Row 1", Vector) = (0,1,0,0)
        _OutputPixelSize ("Output Pixel Size", Vector) = (1,1,0,0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float4 _MainTex_TexelSize, _CameraUV0, _CameraUV1, _OutputPixelSize;
        struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Output Vert(Input v)
        {
            Output o; o.position = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
        }
        float2 CameraUV(float2 uv)
        {
            return float2(dot(_CameraUV0.xy, uv) + _CameraUV0.z, dot(_CameraUV1.xy, uv) + _CameraUV1.z);
        }
        float4 SampleCamera(float2 uv)
        {
            float2 edge = abs(_MainTex_TexelSize.xy) * 0.5;
            return float4(tex2D(_MainTex, clamp(CameraUV(uv), edge, 1 - edge)).rgb, 1);
        }
        float4 Preview(Output input) : SV_Target { return SampleCamera(input.uv); }
        float4 Downsample(Output input) : SV_Target
        {
            float2 sourcePixels = abs(_MainTex_TexelSize.zw);
            float2 footprint = float2(length(float2(_CameraUV0.x, _CameraUV1.x) * sourcePixels),
                length(float2(_CameraUV0.y, _CameraUV1.y) * sourcePixels)) * _OutputPixelSize.xy;
            float2 count = clamp(ceil(footprint * 0.5), 1, 4);
            float3 sum = 0;
            [unroll] for (int y = 0; y < 4; y++)
            {
                [unroll] for (int x = 0; x < 4; x++)
                {
                    if (x < count.x && y < count.y)
                    {
                        float2 offset = ((float2(x, y) + 0.5) / count - 0.5) * _OutputPixelSize.xy;
                        sum += SampleCamera(input.uv + offset).rgb;
                    }
                }
            }
            return float4(sum / (count.x * count.y), 1);
        }
        ENDCG
        Pass
        {
            Name "Preview"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Preview
            ENDCG
        }
        Pass
        {
            Name "FilteredDownsample"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Downsample
            ENDCG
        }
    }
}

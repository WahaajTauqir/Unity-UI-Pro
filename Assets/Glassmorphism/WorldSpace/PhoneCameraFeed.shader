Shader "Glassmorphism/PhoneCameraFeed"
{
    Properties
    {
        _CameraTex ("Camera Feed", 2D) = "black" {}
        _CameraUV0 ("Camera UV Row 0", Vector) = (1,0,0,0)
        _CameraUV1 ("Camera UV Row 1", Vector) = (0,1,0,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "CameraBackground"
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Background
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            TEXTURE2D(_CameraTex);
            float4 _CameraTex_TexelSize, _CameraUV0, _CameraUV1;

            half4 Background(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                uv = float2(dot(_CameraUV0.xy, uv) + _CameraUV0.z,
                    dot(_CameraUV1.xy, uv) + _CameraUV1.z);
                float2 edge = abs(_CameraTex_TexelSize.xy) * 0.5;
                return half4(SAMPLE_TEXTURE2D_LOD(_CameraTex, sampler_LinearClamp, clamp(uv, edge, 1-edge), 0).rgb, 1);
            }
            ENDHLSL
        }
    }
}

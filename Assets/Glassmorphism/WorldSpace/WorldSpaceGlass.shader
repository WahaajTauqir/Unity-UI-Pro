Shader "Glassmorphism/WorldSpaceGlass"
{
    Properties
    {
        [PerRendererData] _MainTex ("Backdrop", 2D) = "white" {}
        _OverlayTex ("Panel Image", 2D) = "white" {}
        [HideInInspector] _PanelRect ("Panel Rect", Vector) = (0,0,1,1)
        [HideInInspector] _PanelCameraID ("Viewing Camera", Float) = 0
        [HideInInspector] _OverlayRect ("Image Rect", Vector) = (0,0,1,1)
        [HideInInspector] _OverlayUV ("Image UV", Vector) = (0,0,1,1)
        [HideInInspector] _OverlayColor ("Image Color", Color) = (1,1,1,1)
        [HideInInspector] _OverlayEnabled ("Image Enabled", Float) = 0
        [HideInInspector] _MaskWithImage ("Mask With Image", Float) = 1
        [HideInInspector] _TintColor ("Tint", Color) = (0,0,0,0)
        [HideInInspector] _BorderColor ("Border Color", Color) = (1,1,1,0.5)
        [HideInInspector] _CornerRadius ("Corner Radius", Float) = 0
        [HideInInspector] _BorderWidth ("Border Width", Float) = 0
        [HideInInspector] _ImageSliced ("Sliced Image", Float) = 0
        [HideInInspector] _SliceBorder ("Destination Borders", Vector) = (0,0,0,0)
        [HideInInspector] _SliceUVBorder ("Source Borders", Vector) = (0,0,0,0)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #include "../Shaders/GlassImageSlicing.hlsl"

            // Array texture for SPI/multiview, regular 2D for mono/multipass.
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_WorldGlassBackdrop);
            sampler2D _OverlayTex;
            float _WorldGlassCameraID, _PanelCameraID;
            float4 _PanelRect, _OverlayRect, _OverlayUV, _ClipRect;
            fixed4 _OverlayColor, _TintColor, _BorderColor, _TextureSampleAdd;
            float _OverlayEnabled, _MaskWithImage, _CornerRadius, _BorderWidth;

            struct Attributes
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 position : SV_POSITION;
                fixed4 color : COLOR;
                float2 panelUV : TEXCOORD0;
                float4 screenPosition : TEXCOORD2;
                float2 localPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(Varyings, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.position = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.panelUV = v.uv;
                o.screenPosition = ComputeScreenPos(o.position);
                o.localPosition = v.vertex.xy;
                return o;
            }
            fixed4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // Perspective-correct screen projection keeps the backdrop aligned
                // when the world canvas rotates, moves, or crosses the viewport.
                clip(_PanelCameraID == 0 || _WorldGlassCameraID != _PanelCameraID ? -1 : 1);
                float2 localUV = i.panelUV;
                float2 screenUV = i.screenPosition.xy / i.screenPosition.w;
                fixed4 color = fixed4(UNITY_SAMPLE_SCREENSPACE_TEXTURE(_WorldGlassBackdrop, screenUV).rgb, 1);
                color.rgb = lerp(color.rgb, _TintColor.rgb, _TintColor.a);
                if (_OverlayEnabled > 0.5)
                {
                    float2 imageUV = (localUV - _OverlayRect.xy) / _OverlayRect.zw;
                    float inside = step(0, imageUV.x) * step(imageUV.x, 1) * step(0, imageUV.y) * step(imageUV.y, 1);
                    float2 sampledUV = saturate(imageUV);
                    if (_ImageSliced > 0.5) sampledUV = GlassSliceUV(sampledUV);
                    float2 atlasUV = lerp(_OverlayUV.xy, _OverlayUV.zw, sampledUV);
                    fixed4 imageSample = tex2D(_OverlayTex, atlasUV);
                    float overlayAlpha = imageSample.a * _OverlayColor.a * inside;
                    #ifdef UNITY_COLORSPACE_GAMMA
                    color.rgb = lerp(color.rgb, imageSample.rgb * _OverlayColor.rgb, overlayAlpha);
                    #else
                    // Composite the decoration in display space so translucent
                    // artwork matches sRGB previews rather than looking washed out.
                    // Unity converts Color properties to linear in this variant;
                    // recover their display-space values too. Alpha is unchanged.
                    if (overlayAlpha > 0)
                    {
                        float3 backdropSRGB = float3(LinearToGammaSpaceExact(color.r),
                            LinearToGammaSpaceExact(color.g), LinearToGammaSpaceExact(color.b));
                        float3 overlaySRGB = float3(LinearToGammaSpaceExact(_OverlayColor.r),
                            LinearToGammaSpaceExact(_OverlayColor.g), LinearToGammaSpaceExact(_OverlayColor.b));
                        float3 imageSRGB = float3(LinearToGammaSpaceExact(imageSample.r),
                            LinearToGammaSpaceExact(imageSample.g), LinearToGammaSpaceExact(imageSample.b)) * overlaySRGB;
                        float3 blended = lerp(backdropSRGB, imageSRGB, overlayAlpha);
                        color.rgb = float3(GammaToLinearSpaceExact(blended.r),
                            GammaToLinearSpaceExact(blended.g), GammaToLinearSpaceExact(blended.b));
                    }
                    #endif
                    // A decorative sprite can have a translucent fill. Its alpha
                    // defines the mask's silhouette, not the backdrop's opacity.
                    // Match uGUI Mask's alpha cutoff while smoothing the boundary.
                    float maskCoverage = smoothstep(0.001,
                        0.001 + max(fwidth(imageSample.a), 0.0001), imageSample.a);
                    color.a *= lerp(1, maskCoverage * inside, _MaskWithImage);
                }
                if (_CornerRadius > 0 || _BorderWidth > 0)
                {
                    float2 halfSize = _PanelRect.zw * 0.5;
                    float radius = min(_CornerRadius, min(halfSize.x, halfSize.y));
                    float2 q = abs((localUV - 0.5) * _PanelRect.zw) - halfSize + radius;
                    float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
                    float aa = max(fwidth(d), 0.0001);
                    float body = 1 - smoothstep(-aa, 0, d);
                    float inner = 1 - smoothstep(-aa, 0, d + _BorderWidth);
                    color.rgb = lerp(color.rgb, _BorderColor.rgb, (body - inner) * _BorderColor.a);
                    color.a *= body;
                }
                color *= i.color;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(i.localPosition, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
}

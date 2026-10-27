Shader "SwaKaar3DIY/Reconstruction/ColoredPoints"
{
    Properties { _PointSize("Point size (GLES pixels)", Range(1,8)) = 2 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry+1" }
        Pass
        {
            Name "ColoredPoints"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float _PointSize;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float pointSize : PSIZE; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                output.color.rgb = SRGBToLinear(output.color.rgb);
                #endif
                output.pointSize = _PointSize;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return half4(input.color.rgb, 1); }
            ENDHLSL
        }
    }
}
// Overlay unlit de silhueta (fresnel): transparente de frente, opaco na borda. Usado pelo
// ItemBlink via ItemRim.mat — o alpha de _BaseColor é animado por MaterialPropertyBlock,
// então o nome da propriedade tem que ser _BaseColor, igual ao URP/Unlit.
Shader "ItemFeedback/Rim"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Power ("Rim Power", Range(0.5, 8)) = 3
        _Bias ("Rim Bias", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Rim"
            Tags { "LightMode" = "UniversalForward" }

            // Mesmo mesh do item, desenhado depois: LEqual passa na igualdade de profundidade.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Power;
                half _Bias;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewDirWS  : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half ndv = saturate(dot(normalize(input.normalWS), normalize(input.viewDirWS)));
                half rim = pow(1.0h - ndv, _Power);
                rim = saturate(rim + _Bias);
                return half4(_BaseColor.rgb, _BaseColor.a * rim);
            }
            ENDHLSL
        }
    }

    Fallback Off
}

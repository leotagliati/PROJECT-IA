// Vidro com reflexo planar. A cor do vidro escurece o que está atrás (alpha) e o reflexo da PlanarReflection é
// SOMADO por cima: vidro real não tapa a luz refletida, só acrescenta. Por isso o blend é pré-multiplicado.
Shader "PROJECT-IA/GlassReflection"
{
    Properties
    {
        _Tint ("Tint (alpha = quanto escurece)", Color) = (0.1, 0.13, 0.13, 0.25)
        _ReflectionStrength ("Reflection Strength", Range(0, 1)) = 0.3
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 3
        _FresnelBoost ("Fresnel Boost (reflexo extra de lado)", Range(0, 1)) = 0.4
        [HideInInspector] _ReflectionTex ("Reflection", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Glass"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
            };

            TEXTURE2D(_ReflectionTex);
            SAMPLER(sampler_ReflectionTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _ReflectionStrength;
                half _FresnelPower;
                half _FresnelBoost;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.screenPos = ComputeScreenPos(position.positionCS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // A câmera do reflexo usa a mesma projeção da câmera do player: o pixel da tela é o pixel do reflexo.
                float2 uv = input.screenPos.xy / input.screenPos.w;
                // A PlanarReflection espelha a projeção em X (para não inverter o culling): desfaz aqui.
                uv.x = 1.0 - uv.x;
                half3 reflection = SAMPLE_TEXTURE2D(_ReflectionTex, sampler_ReflectionTex, uv).rgb;

                // abs: o vidro é visto dos dois lados (Cull Off).
                float3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));
                half facing = abs(dot(normalize(input.normalWS), viewDir));
                half fresnel = pow(1.0h - saturate(facing), _FresnelPower);
                half amount = saturate(_ReflectionStrength + fresnel * _FresnelBoost);

                // A neblina apaga o vidro inteiro: a cor puxa para a cor da neblina e o reflexo some com a distância.
                half3 tint = MixFog(_Tint.rgb, input.fogFactor) * _Tint.a;
                // MixFogColor para preto, e não ComputeFogIntensity: sem neblina na cena aquele devolve 0 e apagaria o
                // reflexo; este devolve a cor intacta.
                half3 added = MixFogColor(reflection * amount, half3(0, 0, 0), input.fogFactor);

                return half4(tint + added, _Tint.a);
            }
            ENDHLSL
        }
    }
}

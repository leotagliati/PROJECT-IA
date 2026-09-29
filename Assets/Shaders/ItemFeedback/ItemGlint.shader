// Overlay unlit com uma faixa de luz que atravessa o objeto periodicamente (o "brilho de
// espada"). A faixa é uma função do espaço de MUNDO projetado em _Direction, repetida a
// cada _Span metros e deslocada por tempo — assim o mesmo material serve para itens de
// qualquer tamanho, e cada item recebe um _Phase diferente via MaterialPropertyBlock para
// não passarem em uníssono. Animado no shader: o ItemGlint só liga/desliga o overlay.
Shader "ItemFeedback/Glint"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Direction ("Sweep Direction (world)", Vector) = (1, 1, 0, 0)
        _Span ("Distance Between Sweeps (m)", Float) = 3
        _Speed ("Sweep Speed (m/s)", Float) = 2.5
        _Width ("Band Width (0-0.5, fraction of span)", Range(0.005, 0.5)) = 0.06
        _Phase ("Phase", Range(0, 1)) = 0
        _RimBoost ("Rim Boost", Range(0, 1)) = 0.35
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
            Name "Glint"
            Tags { "LightMode" = "UniversalForward" }

            // Aditivo: a faixa CLAREIA o que está embaixo em vez de cobrir — parece reflexo.
            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _Direction;
                float _Span;
                float _Speed;
                float _Width;
                float _Phase;
                half _RimBoost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewDirWS  : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(_Direction.xyz);
                float d = dot(input.positionWS, dir) - _Time.y * _Speed;

                // 0..1 dentro de cada período de _Span metros; a faixa fica centrada em 0.5.
                float u = frac(d / max(_Span, 0.001) + _Phase);
                float band = 1.0 - smoothstep(0.0, _Width, abs(u - 0.5));

                // Um pouco de fresnel: a faixa "pega" mais nas bordas, como reflexo real.
                half ndv = saturate(dot(normalize(input.normalWS), normalize(input.viewDirWS)));
                half rim = lerp(1.0h, 1.0h - ndv, _RimBoost);

                return half4(_BaseColor.rgb, _BaseColor.a * band * rim);
            }
            ENDHLSL
        }
    }

    Fallback Off
}

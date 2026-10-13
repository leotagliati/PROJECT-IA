// Detecta borda na máscara do OutlineMask (renderizada SEM teste contra a cena) e compõe sobre a
// cor da câmera.
//
// A versão anterior desenhava a máscara já ocluída e tirava a borda da profundidade da CENA. Aí
// qualquer coisa na frente do objeto (o próprio corpo do player) abria um buraco na máscara, e a
// silhueta desse buraco virava contorno. Aqui a máscara é inteira, e a cena só decide visibilidade:
// - oclusor colado no objeto (batente, parede): conta como fundo, então a borda visível é contornada;
// - oclusor afastado (corpo, móvel na frente): conta como objeto, sem borda, e o pixel é escondido.
Shader "Hidden/Xenon/OutlineComposite"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
        _OutlineScale ("Outline Scale (px)", Float) = 1
        _NormalThreshold ("Normal Threshold", Float) = 1
        _DepthThreshold ("Depth Threshold (relativo)", Float) = 0.1
        _OccluderGap ("Occluder Gap (m)", Float) = 0.15
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "OutlineComposite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _OutlineColor;
            float _OutlineScale;
            float _NormalThreshold;
            float _DepthThreshold;
            float _OccluderGap;

            static const float BackgroundDepth = 1e4;

            static const int2 Directions[8] =
            {
                int2(1, 0), int2(-1, 0), int2(0, 1), int2(0, -1),
                int2(1, 1), int2(-1, 1), int2(1, -1), int2(-1, -1)
            };

            struct Tap
            {
                bool isObject;
                float3 normal;
                float depth;
            };

            Tap SampleTap(int2 pixel)
            {
                pixel = clamp(pixel, int2(0, 0), int2(_ScreenSize.xy) - 1);

                float4 mask = LOAD_TEXTURE2D_X(_BlitTexture, pixel);
                float sceneDepth = LinearEyeDepth(LoadSceneDepth(pixel), _ZBufferParams);

                // Folga para a própria superfície não se ocluir por precisão de depth.
                float bias = 0.02 + mask.w * 0.002;
                bool covered = sceneDepth < mask.w - bias;
                bool coveredByContact = covered && sceneDepth > mask.w - _OccluderGap;

                Tap tap;
                tap.isObject = mask.w > 0 && !coveredByContact;
                tap.normal = tap.isObject ? mask.xyz : 0;
                tap.depth = tap.isObject ? mask.w : BackgroundDepth;
                return tap;
            }

            bool IsCrease(Tap a, Tap b, float referenceDepth)
            {
                if (!a.isObject || !b.isObject)
                    return false;

                return length(a.normal - b.normal) > _NormalThreshold
                    || abs(a.depth - b.depth) > _DepthThreshold * referenceDepth;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int2 center = int2(input.positionCS.xy);
                int h = clamp((int)round(_OutlineScale), 1, 8);
                Tap self = SampleTap(center);

                float nearest = BackgroundDepth;
                bool edge = false;

                if (self.isObject)
                {
                    // Dentro do objeto só existe quina (Roberts cross nas diagonais). A silhueta é
                    // desenhada do lado de fora.
                    Tap a = SampleTap(center + int2(-1, -1));
                    Tap b = SampleTap(center + int2(1, -1));
                    Tap c = SampleTap(center + int2(-1, 1));
                    Tap d = SampleTap(center + int2(1, 1));

                    nearest = self.depth;
                    edge = IsCrease(a, d, nearest) || IsCrease(b, c, nearest);
                }
                else
                {
                    // Anel por FORA: algum vizinho a até h pixels é objeto. Amostrar só os cantos a
                    // ±h (como antes) pulava peças com menos de 2h px, e na RenderTexture de 512 px
                    // isso virava duas linhas paralelas com a peça sem contorno no meio.
                    [loop]
                    for (int r = 1; r <= h; r++)
                    {
                        [unroll]
                        for (int i = 0; i < 8; i++)
                        {
                            Tap tap = SampleTap(center + Directions[i] * r);
                            if (tap.isObject)
                            {
                                edge = true;
                                nearest = min(nearest, tap.depth);
                            }
                        }
                    }
                }

                if (!edge)
                    return 0;

                float centerSceneDepth = LinearEyeDepth(LoadSceneDepth(center), _ZBufferParams);
                if (centerSceneDepth < nearest - _OccluderGap)
                    return 0;

                return float4(_OutlineColor.rgb, _OutlineColor.a);
            }
            ENDHLSL
        }
    }
}

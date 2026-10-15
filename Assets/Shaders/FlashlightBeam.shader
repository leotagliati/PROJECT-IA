// Feixe "volumétrico" da lanterna. O URP não tem volumetria, então o cone (malha do
// FlashlightBeam) é só o volume onde o raio da câmera é marchado: cada pixel soma a densidade
// do cone entre a câmera e o que vier primeiro — a parede do fundo do cone ou a geometria da
// cena (depth texture). É isso que faz o feixe parar na parede em vez de atravessá-la.
//
// Cull Front + ZTest Always: só a face interna, uma vez por pixel, e sem ser cortada pela
// parede que o feixe atinge (quem corta é o min com a profundidade da cena, no fragment).
Shader "PROJECT-IA/FlashlightBeam"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.96, 0.86, 1)
        _Intensity ("Intensity", Float) = 0.1
        _Length ("Length", Float) = 10
        _TanAngle ("Tan Half Angle", Float) = 0.4
        _NearFade ("Near Fade", Float) = 0.6
        _NoiseAmount ("Noise Amount", Range(0, 1)) = 0.5
        _NoiseScale ("Noise Scale", Float) = 1.5
        _NoiseDrift ("Noise Drift", Vector) = (0.05, 0.08, 0.03, 0)
        _FogParams ("Fog (mode, density, start, end)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Beam"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // Poucos passos + jitter por pixel: o grão que sobra combina com a pixelização.
            #define STEPS 12

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Intensity;
                float _Length;
                float _TanAngle;
                float _NearFade;
                float _NoiseAmount;
                float _NoiseScale;
                float4 _NoiseDrift;
                float4 _FogParams;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                return lerp(
                    lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x),
                         lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                         lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }

            // Mesma neblina da cena (RenderSettings, repassada pelo FlashlightBeam): sem isso o
            // feixe aditivo brilharia dentro da neblina que apaga todo o resto.
            float FogTransmittance(float distance)
            {
                float mode = _FogParams.x;
                float density = _FogParams.y;

                if (mode < 0.5) return 1.0;
                if (mode < 1.5) return saturate((_FogParams.w - distance) / max(_FogParams.w - _FogParams.z, 1e-3));
                if (mode < 2.5) return exp(-density * distance);

                float d = density * distance;
                return exp(-d * d);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 screenUV = input.positionCS.xy / _ScaledScreenParams.xy;
                float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);

                float3 cameraWS = GetCameraPositionWS();
                float3 toSurface = input.positionWS - cameraWS;
                float surfaceDistance = length(toSurface);
                float3 rayWS = toSurface / surfaceDistance;

                // Profundidade de olho é ao longo do forward da câmera; o raio é inclinado.
                float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
                float sceneDistance = sceneEyeDepth / max(dot(rayWS, cameraForward), 1e-3);
                float endDistance = min(surfaceDistance, sceneDistance);

                // Marcha em espaço de objeto: ápice na origem, eixo +Z. A malha não tem escala.
                float3 originOS = TransformWorldToObject(cameraWS);
                float3 rayOS = TransformWorldToObjectDir(rayWS);

                float stepLength = endDistance / STEPS;
                float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))));
                float density = 0.0;

                [unroll]
                for (int i = 0; i < STEPS; i++)
                {
                    float t = (i + jitter) * stepLength;
                    float3 p = originOS + rayOS * t;

                    float along = p.z;
                    float radius = max(along * _TanAngle, 1e-4);
                    float cone = saturate(1.0 - length(p.xy) / radius);
                    cone *= cone;

                    float axial = saturate(1.0 - along / _Length);
                    axial *= axial;

                    float3 pWS = cameraWS + rayWS * t;
                    float noise = ValueNoise(pWS * _NoiseScale + _Time.y * _NoiseDrift.xyz);

                    density += cone * axial
                             * step(0.0, along)
                             * saturate(t / _NearFade)
                             * lerp(1.0, noise * 2.0, _NoiseAmount)
                             * FogTransmittance(t);
                }

                density *= stepLength;
                return half4(_Color.rgb * density * _Intensity, 0);
            }
            ENDHLSL
        }
    }
}

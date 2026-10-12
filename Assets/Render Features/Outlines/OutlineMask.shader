// Material de override do OutlinePassFilter. Grava normal em view space (xyz) e profundidade
// linear (w) num buffer próprio; w > 0 é a máscara do objeto destacado.
Shader "Hidden/Xenon/OutlineMask"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "OutlineMask"
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalVS : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionVS = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
                output.positionCS = TransformWViewToHClip(positionVS);
                output.normalVS = TransformWorldToViewNormal(TransformObjectToWorldNormal(input.normalOS));
                output.eyeDepth = -positionVS.z;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(normalize(input.normalVS), max(input.eyeDepth, 1e-4));
            }
            ENDHLSL
        }
    }
}

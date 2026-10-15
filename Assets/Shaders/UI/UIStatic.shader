// Estática em escala de cinza para Image de UI (pause/settings). Não dá para reaproveitar o
// StaticShader.shadergraph aqui: ele é um Unlit opaco de malha 3D (tela da TV / cabeça do
// Seeker) e não entende cor do Image, stencil de máscara nem clip rect.
//
// O padrão é o Gradient Noise do graph em escala alta. Girar a UV como o graph faz não serve
// aqui: em tela cheia a rotação em torno de um centro distante vira um deslize coerente (parece
// um disco girando), e hash por pixel vira listras. Por isso cada quadro ganha um deslocamento
// aleatório. A UV é a de tela (com aspecto corrigido), porque o Image do menu não tem UV útil.
//
// A cor/alpha do Image (vertex color) continua mandando na escuridão e na opacidade do painel;
// o ruído entra por cima sem tingir, e o jogo continua visível por baixo.
//
// O quadro usa _UnscaledTime (setado pelo UnscaledShaderTime), porque o pause zera
// Time.timeScale e o _Time do shader congelaria a estática.
Shader "UI/Static"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)

        _NoiseStrength ("Noise Strength", Range(0, 1)) = 0.25
        _Scale ("Noise Scale", Float) = 200
        _NoiseFps ("Noise FPS", Float) = 24
        _Contrast ("Contrast", Float) = 2.5

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
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

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
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;
            float4 _MainTex_ST;

            float _NoiseStrength;
            float _Scale;
            float _NoiseFps;
            float _Contrast;

            // Setado globalmente pelo UnscaledShaderTime.
            float _UnscaledTime;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            // Gradient Noise do Shader Graph (mesma implementação do nó).
            float2 gradientNoiseDir(float2 p)
            {
                p = fmod(p, 289.0);
                float x = fmod((34.0 * p.x + 1.0) * p.x, 289.0) + p.y;
                x = fmod((34.0 * x + 1.0) * x, 289.0);
                x = frac(x / 41.0) * 2.0 - 1.0;
                return normalize(float2(x - floor(x + 0.5), abs(x) - 0.5));
            }

            float gradientNoise(float2 p)
            {
                float2 ip = floor(p);
                float2 fp = frac(p);
                float d00 = dot(gradientNoiseDir(ip), fp);
                float d01 = dot(gradientNoiseDir(ip + float2(0.0, 1.0)), fp - float2(0.0, 1.0));
                float d10 = dot(gradientNoiseDir(ip + float2(1.0, 0.0)), fp - float2(1.0, 0.0));
                float d11 = dot(gradientNoiseDir(ip + float2(1.0, 1.0)), fp - float2(1.0, 1.0));
                fp = fp * fp * fp * (fp * (fp * 6.0 - 15.0) + 10.0);
                return lerp(lerp(d00, d10, fp.x), lerp(d01, d11, fp.x), fp.y) + 0.5;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 tex = tex2D(_MainTex, IN.texcoord);
                half4 color = IN.color * tex;

                // UV de tela com aspecto corrigido: o grão sai quadrado em qualquer resolução.
                float2 uv = IN.vertex.xy / _ScreenParams.y;

                // Um deslocamento aleatório novo a cada "quadro": cada quadro é um recorte sem
                // relação com o anterior, então não há movimento coerente para o olho seguir.
                float frame = fmod(floor(_UnscaledTime * _NoiseFps), 4096.0);
                float2 offset = float2(frac(sin(frame * 12.9898) * 43758.5453),
                                       frac(sin(frame * 78.2330) * 24634.6345)) * 1000.0;

                float noise = gradientNoise(uv * _Scale + offset);
                noise = saturate((noise - 0.5) * _Contrast + 0.5);

                color.rgb = lerp(color.rgb, noise.xxx, _NoiseStrength);

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
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

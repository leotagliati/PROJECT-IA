// Acabamento PS1 na imagem final: o RawImage que mostra a RenderTexture do player usa este
// material. Três coisas, todas na resolução da RenderTexture (o "pixel grande"), não da tela:
//  - amostra no centro do texel: a RT é filtrada em Bilinear, e esticada na tela vira borrão;
//  - quantiza cada canal em poucos níveis (o PS1 tinha 5 bits por canal);
//  - dithering ordenado Bayer 4x4 antes de quantizar, que troca as faixas de degradê pelo
//    xadrez característico. Sem ele, quantizar o escuro do jogo vira degraus duros.
// A quantização é feita em espaço gamma: o projeto é Linear, e níveis iguais em linear gastam
// quase todos os degraus no claro e deixam o escuro — que é quase o jogo inteiro — com dois.
Shader "PROJECT-IA/RetroScreen"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)

        [Toggle] _PixelSnap ("Pixel Snap", Float) = 1
        _Levels ("Levels per Channel", Range(2, 64)) = 24
        _DitherStrength ("Dither Strength", Range(0, 1)) = 1
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

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            float _PixelSnap;
            float _Levels;
            float _DitherStrength;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            float Bayer4(float2 pixel)
            {
                const float bayer[16] =
                {
                     0.0,  8.0,  2.0, 10.0,
                    12.0,  4.0, 14.0,  6.0,
                     3.0, 11.0,  1.0,  9.0,
                    15.0,  7.0, 13.0,  5.0
                };

                int2 p = int2(fmod(pixel, 4.0));
                return bayer[p.y * 4 + p.x] / 16.0 - 0.5 + 1.0 / 32.0;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texels = i.uv * _MainTex_TexelSize.zw;
                float2 pixel = floor(texels);

                float2 uv = _PixelSnap > 0.5 ? (pixel + 0.5) * _MainTex_TexelSize.xy : i.uv;
                fixed4 color = tex2D(_MainTex, uv) * i.color;

                float3 rgb = color.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = LinearToGammaSpace(rgb);
                #endif

                float steps = _Levels - 1.0;
                rgb += Bayer4(pixel) * _DitherStrength / steps;
                rgb = saturate(floor(rgb * steps + 0.5) / steps);

                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = GammaToLinearSpace(rgb);
                #endif

                return fixed4(rgb, color.a);
            }
            ENDCG
        }
    }
}

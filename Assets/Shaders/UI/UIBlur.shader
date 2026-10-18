// Fundo desfocado atrás de modal: um RawImage com a mesma RenderTexture do player (a do CameraView) e este
// material. O jogo continua renderizando com timeScale 0, então o borrão segue o que a câmera vê.
// O raio é em texels da textura, não em pixels da tela: a RT é pequena (512x256), e poucos texels já
// borram bastante. A cor do RawImage multiplica o resultado: escura, vira o "desfoque preto".
Shader "PROJECT-IA/UIBlur"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Radius ("Radius (texels)", Range(0, 8)) = 3
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
            float _Radius;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                // O alpha do CanvasGroup chega pela cor do vértice: o fade do modal leva o borrão junto.
                o.color = v.color * _Color;
                return o;
            }

            // Um passe só, dois anéis de 8 amostras + centro, com peso caindo com a distância. Separável em dois
            // passes seria mais correto, mas UI não tem passe intermediário, e com a RT pequena isto já sobra.
            fixed4 frag(v2f i) : SV_Target
            {
                float2 step = _MainTex_TexelSize.xy * _Radius;
                float3 sum = tex2D(_MainTex, i.uv).rgb * 0.2;
                float total = 0.2;

                [unroll]
                for (int k = 0; k < 8; k++)
                {
                    float angle = k * 0.785398;
                    float2 dir = float2(cos(angle), sin(angle));

                    sum += tex2D(_MainTex, i.uv + dir * step * 0.5).rgb * 0.12;
                    sum += tex2D(_MainTex, i.uv + dir * step).rgb * 0.06;
                    total += 0.18;
                }

                return fixed4(sum / total * i.color.rgb, i.color.a);
            }
            ENDCG
        }
    }
}

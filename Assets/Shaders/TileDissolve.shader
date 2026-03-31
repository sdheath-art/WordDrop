Shader "WordDrop/TileDissolve"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _DissolveAmount ("Dissolve Amount", Range(0, 1)) = 0
        _EdgeWidth ("Edge Width", Range(0, 0.15)) = 0.06
        _EdgeColor ("Edge Color", Color) = (1, 0.55, 0.1, 1)
        _EdgeColor2 ("Edge Color Inner", Color) = (1, 0.9, 0.3, 1)
        _NoiseScale ("Noise Scale", Float) = 8.0
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
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _DissolveAmount;
            float _EdgeWidth;
            float4 _EdgeColor;
            float4 _EdgeColor2;
            float _NoiseScale;

            // Simple hash-based noise (no texture needed)
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f); // smoothstep

                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float fbm(float2 p)
            {
                float val = 0.0;
                float amp = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    val += amp * valueNoise(p);
                    p *= 2.0;
                    amp *= 0.5;
                }
                return val;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv) * i.color;

                // Generate noise pattern
                float noise = fbm(i.uv * _NoiseScale);

                // Dissolve threshold
                float threshold = _DissolveAmount;

                // Clip dissolved pixels
                float diff = noise - threshold;
                clip(diff);

                // Edge glow — hot border on dissolving edge
                float edge = 1.0 - smoothstep(0.0, _EdgeWidth, diff);
                float innerEdge = 1.0 - smoothstep(0.0, _EdgeWidth * 0.4, diff);

                // Blend edge colors: outer = orange, inner = bright yellow
                float3 edgeCol = lerp(_EdgeColor.rgb, _EdgeColor2.rgb, innerEdge);

                // Mix edge glow into the sprite color
                tex.rgb = lerp(tex.rgb, edgeCol, edge);

                // Brighten edge slightly (emissive feel)
                tex.rgb += edgeCol * edge * 0.3;

                // Premultiply alpha for sprite rendering
                tex.rgb *= tex.a;

                return tex;
            }
            ENDCG
        }
    }
}

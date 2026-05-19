Shader "WordDrop/ScreenSprite"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        // Photoshop "Screen" blend: result = 1 - (1-dst)(1-src) — brightens
        // the underlying pixels but never blows past white, unlike additive.
        // Good for a diffused light-overlay feel (Candy-Crush-color-bomb glow).
        // Fragment is pre-multiplied by alpha so transparent regions of the
        // sprite contribute nothing regardless of RGB.
        Blend OneMinusDstColor One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; float4 color : COLOR; };

            sampler2D _MainTex;
            float4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                fixed4 col = tex * i.color;
                // Pre-multiply RGB by alpha so transparent sprite regions
                // contribute (1-0)*0 + dst*1 = dst → no effect, as expected.
                col.rgb *= col.a;
                return col;
            }
            ENDCG
        }
    }
}

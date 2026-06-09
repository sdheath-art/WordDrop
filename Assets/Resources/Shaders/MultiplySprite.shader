// 2026-06-04 Spencer: alpha-aware MULTIPLY blend for sprites (e.g. baked drop
// shadows authored as Multiply layers in Photoshop). Transparent texels multiply
// by white (no change); opaque dark texels multiply the framebuffer darker —
// matching PS's Multiply. The sprite's alpha carries the PS layer opacity.
Shader "WordDrop/MultiplySprite"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color   ("Tint", Color) = (1,1,1,1)
        _Strength ("Strength", Range(0,2)) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend DstColor Zero   // multiply: result = src * dst

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f     { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float  _Strength;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = TRANSFORM_TEX(v.uv, _MainTex);
                o.color  = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv) * _Color * i.color;
                // Alpha-aware multiply factor: a=0 → white (no darkening),
                // a=1 → the texel's (dark) rgb. _Strength scales the effect.
                float a = saturate(tex.a * _Strength);
                fixed3 mult = lerp(fixed3(1,1,1), tex.rgb, a);
                return fixed4(mult, 1);
            }
            ENDCG
        }
    }
}

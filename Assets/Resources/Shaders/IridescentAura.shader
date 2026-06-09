Shader "WordDrop/IridescentAura"
{
    // Soft RAINBOW glow aura for behind the wild tile/card. The sprite alpha is a
    // soft radial falloff (the glow shape); RGB is a pastel rainbow swept by angle
    // around the centre, slowly rotating. Additive + HDR so the scene bloom turns
    // it into a luminous halo. 2026-06-03 Spencer (match the legendary-gem aura).
    Properties
    {
        _MainTex     ("Glow Sprite (alpha = shape)", 2D) = "white" {}
        _Brightness  ("Brightness", Float)               = 2.4
        _Saturation  ("Saturation", Range(0,1))          = 0.75
        _Speed       ("Rotate Speed", Float)             = 0.12
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        Lighting Off
        Blend One One   // additive

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex; float4 _MainTex_ST;
            float _Brightness, _Saturation, _Speed;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);    // alpha = soft glow shape
                float2 c   = i.uv - 0.5;
                float  ang = atan2(c.y, c.x) * 0.1591549 + 0.5;   // 0..1 around the centre
                ang += _Time.y * _Speed;

                // Pastel rainbow (cosine palette).
                float3 col = 0.5 + 0.5 * cos(6.2831853 * (ang + float3(0.0, 0.33, 0.67)));
                float grey = dot(col, float3(0.299, 0.587, 0.114));
                col = lerp(grey.xxx, col, _Saturation);

                float a = tex.a * i.color.a;
                return fixed4(col * _Brightness * a, a);   // additive, HDR → blooms
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}

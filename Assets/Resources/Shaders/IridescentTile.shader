Shader "WordDrop/IridescentTile"
{
    // Smooth "crystal/gem" pastel gradient on the white tile.
    //   Pass 1 (multiply): tints the tile cyan → gold → pink while keeping its
    //                      3D form (bevel/gloss/shadow) intact.
    //   Pass 2 (additive): a SUBTLE glow so the tile reads as luminous — the
    //                      brightest crystal areas cross the bloom line and bloom.
    // Diagonal cyan(bottom-left) → gold(centre) → pink(top-right), gently drifting.
    // 2026-06-03 Spencer.
    Properties
    {
        _MainTex     ("Sprite (alpha = tile shape)", 2D) = "white" {}
        _Strength    ("Tint Strength", Range(0,2.5))     = 1.0
        _Saturation  ("Saturation", Range(0,1.5))        = 1.0
        _Brightness  ("Brightness", Float)               = 1.0
        _GlowStrength("Glow Strength", Range(0,1))       = 0.35
        _SwirlSpeed  ("Drift Speed", Float)              = 0.25
        _DriftAmt    ("Drift Amount", Float)             = 0.10
        _RimBoost    ("Edge Sheen", Float)               = 0.10
        _HueShift    ("Gradient Offset", Float)          = 0.0
        _Tint        ("Tint", Color)                     = (1.0, 1.0, 1.0, 1.0)
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
    struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

    sampler2D _MainTex; float4 _MainTex_ST;
    float _Strength, _Saturation, _Brightness, _GlowStrength, _SwirlSpeed, _DriftAmt, _RimBoost, _HueShift;
    fixed4 _Tint;

    v2f vert (appdata v)
    {
        v2f o;
        o.pos   = UnityObjectToClipPos(v.vertex);
        o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
        o.color = v.color;
        return o;
    }

    float3 crystalRamp(float t)
    {
        float3 cyan = float3(0.50, 0.90, 1.00);
        float3 gold = float3(1.00, 0.93, 0.58);
        float3 pink = float3(1.00, 0.66, 0.93);
        return (t < 0.5) ? lerp(cyan, gold, smoothstep(0.0, 1.0, t * 2.0))
                         : lerp(gold, pink, smoothstep(0.0, 1.0, (t - 0.5) * 2.0));
    }

    // The crystal colour at this uv (shared by both passes).
    float3 crystalColor (float2 uv)
    {
        float t = (uv.x + uv.y) * 0.5;                                   // bottom-left -> top-right
        t += sin(_Time.y * _SwirlSpeed + uv.x * 1.4) * _DriftAmt + _HueShift;
        float3 col = crystalRamp(saturate(t));
        float grey = dot(col, float3(0.299, 0.587, 0.114));
        col = lerp(grey.xxx, col, _Saturation);
        return col * _Brightness * _Tint.rgb;
    }
    ENDCG

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off ZWrite Off Lighting Off

        // ── Pass 1: MULTIPLY tint (keeps the tile's form) ──
        Pass
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                float3 col = crystalColor(i.uv);

                // Subtle bright edge sheen (gem bevel).
                float2 c = i.uv - 0.5;
                float  r = saturate(length(c) * 2.0);
                col += pow(r, 3.0) * _RimBoost;

                col = lerp(float3(1,1,1), col, _Strength);
                float3 outCol = lerp(float3(1,1,1), col, tex.a); // mask: outside = white = no change
                return fixed4(outCol, 1.0);
            }
            ENDCG
        }

        // ── Pass 2: ADDITIVE subtle glow (bloom catches the bright areas) ──
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                float3 col = crystalColor(i.uv);
                float  a   = tex.a * i.color.a;
                return fixed4(col * _GlowStrength * a, a);    // soft additive glow, masked
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}

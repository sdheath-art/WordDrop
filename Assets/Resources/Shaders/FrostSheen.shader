Shader "WordDrop/FrostSheen"
{
    // Frozen-tile overlay: the translucent blue frost body PLUS a specular "sheen" band that
    // rakes diagonally across the ice every few seconds — like light glancing off gold foil.
    // SINGLE PASS (URP's 2D renderer only draws a sprite material's first pass, so the body and
    // the sweep must be composited in one fragment). Self-animates on the GPU via _Time.y (no
    // per-frame C#). _Phase desyncs each tile. 2026-06-18 Spencer.
    Properties
    {
        _MainTex      ("Sprite (alpha = ice shape)", 2D) = "white" {}
        _Tint         ("Frost Tint", Color)              = (1,1,1,1)
        _SweepColor   ("Sheen Color", Color)             = (0.88, 0.97, 1.0, 1.0)
        _SweepSpeed   ("Sweeps / sec", Float)            = 0.3
        _SweepDuty    ("Active fraction of cycle", Range(0.05,1)) = 0.22
        _SweepWidth   ("Band width", Range(0.02,1.5))    = 1.5
        _SweepStrength("Sheen strength", Range(0,3))     = 0.1
        _Phase        ("Per-tile phase", Range(0,1))     = 0.0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off ZWrite Off Lighting Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex; float4 _MainTex_ST;
            fixed4 _Tint, _SweepColor;
            float _SweepSpeed, _SweepDuty, _SweepWidth, _SweepStrength, _Phase;

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
                fixed4 tex = tex2D(_MainTex, i.uv);
                float3 rgb = tex.rgb * _Tint.rgb * i.color.rgb;   // frost body
                float  a   = tex.a * _Tint.a * i.color.a;

                // Diagonal coordinate: 0 at top-left, 1 at bottom-right (light rakes down-right).
                float s = (i.uv.x + (1.0 - i.uv.y)) * 0.5;

                // Looping cycle; only the first _SweepDuty of it shows the sweep (rest = dark gap).
                float cyc = frac(_Time.y * _SweepSpeed + _Phase);
                float inSweep = (cyc < _SweepDuty) ? 1.0 : 0.0;

                // Band travels from just off the top-left edge to just off the bottom-right edge.
                float pos = (cyc / max(_SweepDuty, 1e-4)) * (1.0 + 2.0 * _SweepWidth) - _SweepWidth;

                float d    = abs(s - pos);
                float core = pow(saturate(1.0 - d / _SweepWidth), 6.0);                  // tight glint
                float halo = pow(saturate(1.0 - d / (_SweepWidth * 2.5)), 2.0) * 0.35;   // soft surround
                float d2   = abs(s - (pos - 0.12));
                float streak = pow(saturate(1.0 - d2 / (_SweepWidth * 0.55)), 8.0) * 0.45; // trailing streak

                float band = (core + halo + streak) * inSweep * _SweepStrength;

                // Brighten toward the sheen colour AND push alpha up so the band reads as a bright,
                // near-opaque specular streak over the translucent frost. Masked to the ice shape.
                rgb += _SweepColor.rgb * band * tex.a;
                a    = saturate(a + band * tex.a * 0.3);
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}

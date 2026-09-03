Shader "WordDrop/IconHSV"
{
    // Hue / saturation / value adjustment for the 3D icon sprites (coin, heart, star).
    //
    // WHY A SHADER: Image.color can only MULTIPLY the texture. That can darken and it can
    // warm a colour, but it cannot rotate hue and it cannot RAISE saturation or lightness —
    // multiplying by anything >1 is clamped, and multiplying by a tint always moves toward
    // that tint rather than shifting the underlying colour. Proper HSV needs the conversion
    // done per-pixel, which is what this does. 2026-07-30.
    //
    // Works for BOTH UI Image and world SpriteRenderer: same alpha-blended sprite setup,
    // and it multiplies by the vertex colour so Image.color / SpriteRenderer.color still
    // apply on top (fade tweens keep working).
    Properties
    {
        _MainTex   ("Texture", 2D) = "white" {}
        _Color     ("Tint", Color) = (1,1,1,1)
        _HueShift  ("Hue Shift", Range(-0.5, 0.5)) = 0        // -0.5..0.5 = a full turn either way
        _Saturation("Saturation", Range(0, 2)) = 1            // 1 = unchanged, 0 = greyscale, 2 = double
        _Value     ("Brightness", Range(0, 2)) = 1            // multiply: darkens, lifts midtones
        _Lightness ("Lightness", Range(-1, 1)) = 0            // wash toward white(+) / black(-)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f     { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _HueShift, _Saturation, _Value, _Lightness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = TRANSFORM_TEX(v.uv, _MainTex);
                o.color  = v.color * _Color;
                return o;
            }

            float3 RGBtoHSV(float3 c)
            {
                float4 K = float4(0.0, -1.0/3.0, 2.0/3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HSVtoRGB(float3 c)
            {
                float4 K = float4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                // Work on straight colour, then re-apply alpha. Adjusting a premultiplied
                // value would tint the transparent fringe and halo the edges.
                float3 hsv = RGBtoHSV(saturate(tex.rgb));
                hsv.x = frac(hsv.x + _HueShift + 1.0);
                hsv.y = saturate(hsv.y * _Saturation);
                hsv.z = saturate(hsv.z * _Value);
                float3 rgb = HSVtoRGB(hsv);
                // Lightness is a separate control from Brightness on purpose: _Value MULTIPLIES,
                // so on an already-bright pixel (gold sits near value 1) it clamps and cannot lift.
                // This lerps toward white or black instead, which genuinely brightens. step() picks
                // the target so there is no branch. 2026-07-30.
                rgb = lerp(rgb, step(0.0, _Lightness).xxx, abs(_Lightness));
                return fixed4(saturate(rgb), tex.a) * i.color;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}

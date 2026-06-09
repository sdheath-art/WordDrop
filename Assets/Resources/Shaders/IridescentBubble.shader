Shader "WordDrop/IridescentBubble"
{
    // Thin-film interference (soap-bubble iridescence) for a sprite quad.
    // The sprite's alpha (bubble@2x soft gradient) is the bubble MASK; the RGB
    // is generated procedurally: a "film thickness" that varies with radius,
    // angle, and a scrolling value-noise drives a cosine rainbow palette, so
    // the colours band and swirl like a real soap film. Rim-weighted (fresnel-
    // ish) so the edge glows. Premultiplied-alpha blend for clean bloom.
    Properties
    {
        _MainTex     ("Sprite (alpha = bubble shape)", 2D) = "white" {}
        _Brightness  ("Brightness", Float)        = 1.3
        _Saturation  ("Iridescence Strength", Range(0,1)) = 1.0
        _SwirlScale  ("Swirl Scale", Float)       = 3.0
        _SwirlSpeed  ("Swirl Speed", Float)       = 0.6
        _BandScale   ("Band Scale", Float)        = 1.3
        _RimBoost    ("Rim Boost", Float)         = 1.6
        _HueShift    ("Hue Shift", Float)         = 0.0
        _Tint        ("Tint (cast over the iridescence)", Color) = (0.78, 1.0, 1.0, 1.0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        Lighting Off
        Blend One OneMinusSrcAlpha   // premultiplied alpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex; float4 _MainTex_ST;
            float _Brightness, _Saturation, _SwirlScale, _SwirlSpeed, _BandScale, _RimBoost, _HueShift;
            fixed4 _Tint;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            float hash (float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float vnoise (float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float a = hash(i), b = hash(i + float2(1,0)), c = hash(i + float2(0,1)), d = hash(i + float2(1,1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);     // alpha = bubble shape
                float2 c   = i.uv - 0.5;
                float  r   = saturate(length(c) * 2.0); // 0 centre -> 1 rim
                float  ang = atan2(c.y, c.x);
                float  t   = _Time.y * _SwirlSpeed;

                // Film "thickness" — radius gradient + swirling noise + angular ripple.
                float thick = r * _BandScale
                            + vnoise(c * _SwirlScale + t) * 0.6
                            + sin(ang * 3.0 + t * 1.3) * 0.12
                            + _HueShift;

                // Cyan-biased thin-film palette: green & blue ride high (cyan),
                // red only peaks → pink/magenta accents. (IQ cosine palette with
                // a low-red DC bias.)
                float3 irid = float3(0.30, 0.55, 0.62)
                            + float3(0.55, 0.45, 0.40) * cos(6.28318 * (thick + float3(0.0, 0.33, 0.62)));

                // Desaturate toward pastel — a real soap film is a faint sheen,
                // not a saturated rainbow. _Saturation 0 = neutral, 1 = full.
                float grey = dot(irid, float3(0.299, 0.587, 0.114));
                irid = lerp(grey.xxx, irid, _Saturation);

                // Rim-weighted (fresnel-ish) so the edge sings; soft core.
                float  rim = pow(r, 2.0) * _RimBoost + 0.22;
                float3 col = irid * rim * _Brightness * _Tint.rgb;

                float a = tex.a * i.color.a;            // bubble mask * fade
                return fixed4(col * a, a);              // premultiplied
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}

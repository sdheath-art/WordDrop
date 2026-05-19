Shader "WordDrop/ScreenSpriteWarp"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _WarpAmp ("Warp Amplitude", Range(0, 0.2)) = 0.04
        _WarpFreq ("Warp Frequency", Range(0, 30)) = 8.0
        _WarpSpeed ("Warp Speed", Range(0, 5)) = 1.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        // Photoshop "Screen" blend: result = 1 - (1-dst)(1-src) — brightens
        // underlying pixels but doesn't blow past white. Combined with a
        // UV warp driven by _Time so the orb edges ripple/wobble like water.
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
            float _WarpAmp;
            float _WarpFreq;
            float _WarpSpeed;

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
                // Two perpendicular sin waves driven by Time, offsetting
                // sample UVs to ripple the texture organically.
                float t = _Time.y * _WarpSpeed;
                float2 warpedUV = i.uv;
                warpedUV.x += sin(i.uv.y * _WarpFreq + t)        * _WarpAmp;
                warpedUV.y += sin(i.uv.x * _WarpFreq + t * 1.3)  * _WarpAmp;

                // Mask out fragments whose warped UVs fell outside [0,1] —
                // otherwise GPU wrap-mode (Repeat) tiles the texture and
                // shows visible bleed lines at the edges.
                float2 inRange = step(0.0, warpedUV) * step(warpedUV, 1.0);
                float mask = inRange.x * inRange.y;

                fixed4 tex = tex2D(_MainTex, warpedUV);
                fixed4 col = tex * i.color * mask;
                col.rgb *= col.a; // pre-multiply for clean Screen blend
                return col;
            }
            ENDCG
        }
    }
}

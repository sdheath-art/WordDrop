Shader "WordDrop/Fake3D"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _RotX ("X Rotation", Range(-45, 45)) = 0
        _RotY ("Y Rotation", Range(-45, 45)) = 0
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
            float _RotX;
            float _RotY;

            #define PI 3.14159265359
            #define DEG2RAD (PI / 180.0)

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
                // Center UVs around origin (-0.5 to 0.5)
                float2 uv = i.uv - 0.5;

                // Apply perspective foreshortening based on rotation
                float cosY = cos(_RotY * DEG2RAD);
                float cosX = cos(_RotX * DEG2RAD);
                float sinY = sin(_RotY * DEG2RAD);
                float sinX = sin(_RotX * DEG2RAD);

                // Simulate perspective: points further from center shift more
                // This creates the illusion of 3D rotation on a flat surface
                float perspective = 2.0; // perspective strength (like FOV)

                // Z depth at this UV point (simulated surface after rotation)
                float z = 1.0 + (uv.x * sinY + uv.y * sinX) * perspective;

                // Perspective divide — closer points (higher z) appear larger
                float2 perspUV = uv / z;

                // Apply foreshortening (rotation compresses one axis)
                perspUV.x /= max(cosY, 0.1);
                perspUV.y /= max(cosX, 0.1);

                // Back to 0-1 range
                perspUV += 0.5;

                // Boundary check — discard pixels outside the texture
                float2 edge = abs(perspUV - 0.5);
                float mask = step(max(edge.x, edge.y), 0.5);

                fixed4 tex = tex2D(_MainTex, perspUV) * i.color;
                tex.a *= mask;

                // Premultiply alpha
                tex.rgb *= tex.a;

                return tex;
            }
            ENDCG
        }
    }
}

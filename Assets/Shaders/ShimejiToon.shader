// 경량 툰 셰이더 (Built-in RP).
// glTF 임포트 시 "Standard" 대신 사용: Standard의 프로퍼티/키워드(_Color, _MainTex, _Cutoff, _SrcBlend, _DstBlend, _ZWrite,
// _ALPHATEST_ON, _ALPHABLEND_ON, _ALPHAPREMULTIPLY_ON, _EMISSION)와 호환된다.
// Standard를 빌드에 통째로 포함하면 첫 사용 시 수십 MB를 쓰므로, 배리언트 8개짜리 셰이더로 대체한다.
// 알파 채널은 투명 오버레이 합성에 쓰이므로 불투명이면 1, 블렌드면 커버리지를 누적(One, OneMinusSrcAlpha)한다.
Shader "Shimeji/Toon"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionMap ("Emission", 2D) = "white" {}
        _ShadeStrength ("Shade Strength", Range(0,1)) = 0.3
        _RimStrength ("Rim Strength", Range(0,1)) = 0.2
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "IgnoreProjector"="True" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma multi_compile_local __ _EMISSION
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed _Cutoff;
            sampler2D _EmissionMap;
            fixed4 _EmissionColor;
            half _ShadeStrength;
            half _RimStrength;
            fixed4 _LightColor0;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(WorldSpaceViewDir(v.vertex));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv) * _Color;
            #if defined(_ALPHATEST_ON)
                clip(tex.a - _Cutoff);
            #endif
                float3 n = normalize(i.normal);
                float ndl = dot(n, normalize(_WorldSpaceLightPos0.xyz)) * 0.5 + 0.5;
                float lit = lerp(1.0 - _ShadeStrength, 1.0, smoothstep(0.42, 0.52, ndl));
                float3 light = _LightColor0.rgb * lit * 0.85 + UNITY_LIGHTMODEL_AMBIENT.rgb * 0.45;
                float rim = pow(1.0 - saturate(dot(n, normalize(i.viewDir))), 3.0) * _RimStrength;
                float3 col = tex.rgb * light + rim;
            #if defined(_EMISSION)
                col += tex2D(_EmissionMap, i.uv).rgb * _EmissionColor.rgb;
            #endif
            #if defined(_ALPHABLEND_ON) || defined(_ALPHAPREMULTIPLY_ON)
                return fixed4(col, tex.a);
            #else
                return fixed4(col, 1.0);
            #endif
            }
            ENDCG
        }
    }
    FallBack Off
}

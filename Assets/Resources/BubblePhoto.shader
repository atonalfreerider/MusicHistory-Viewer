// An artist photo on a song bubble: a camera-facing disc (billboarded in the vertex shader like
// the bubble itself, so no per-frame transform updates) drawn just in front of the bubble's
// sphere surface and inside its ring, so the decade ring and its glow stay around it. The photo
// is cropped to a centred square (a portrait keeps its upper part, where the faces are), masked
// to a circle with an anti-aliased edge (alpha-to-coverage), lit a little like the sphere and
// dimmed / desaturated with the bubble's state. Smaller than _MinPixels on screen it is not drawn.
// Properties live in the UnityPerMaterial CBUFFER (SRP Batcher).
Shader "MusicHistory/BubblePhoto"
{
    Properties
    {
        _MainTex ("Photo", 2D) = "white" {}
        _Crop ("Crop (uv scale xy, offset zw)", Vector) = (1, 1, 0, 0)
        _DimFactor ("Dim Factor", Range(0, 2)) = 1
        _Saturation ("Saturation", Range(0, 1)) = 1
        _MinPixels ("Hide below (px)", Float) = 12
        _PhotoScale ("Photo / bubble diameter", Range(0.1, 1)) = 0.72
        _DepthBias ("Depth bias (fraction of the bubble radius toward the camera)", Range(0, 0.5)) = 0.02
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest+1" "RenderPipeline"="UniversalPipeline" "DisableBatching"="True" "IgnoreProjector"="True" }
        Cull Off
        ZWrite On
        ZTest LEqual
        AlphaToMask On
        Pass
        {
            Name "Unlit"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Crop;
                float _DimFactor;
                float _Saturation;
                float _MinPixels;
                float _PhotoScale;
                float _DepthBias;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 centerVS : TEXCOORD1;
                float radius : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 axisX = float3(UNITY_MATRIX_M[0].x, UNITY_MATRIX_M[1].x, UNITY_MATRIX_M[2].x);
                // The object is scaled to the photo's diameter; the parent bubble is 1 / scale of it.
                float diameter = length(axisX);
                float3 centerVS = TransformWorldToView(centerWS);
                float pixelWorld = 2.0 * max(-centerVS.z, 1e-3) / (max(_ScreenParams.y, 1.0) * max(abs(UNITY_MATRIX_P[1][1]), 1e-4));
                // Too small to read: collapse the quad (no fragments).
                float show = step(_MinPixels, diameter / pixelWorld);
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 positionWS = centerWS + (right * input.positionOS.x + up * input.positionOS.y) * diameter * show;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;
                output.centerVS = centerVS;
                output.radius = 0.5 * diameter;
                return output;
            }

            half4 frag(Varyings input, out float depth : SV_Depth) : SV_Target
            {
                float2 d = (input.uv - 0.5) * 2.0;          // -1..1 across the disc
                float r = length(d);
                float aa = max(fwidth(r), 1e-4);
                float alpha = saturate((1.0 - r) / aa + 0.5);
                clip(alpha - 0.01);

                // On the bubble's front surface (the photo is the sphere's inner disc: the bubble's
                // radius is the photo's / _PhotoScale), a hair toward the camera so the bubble under
                // it never wins the depth test.
                float scale = max(_PhotoScale, 0.05);
                float bubble = input.radius / scale;
                float rb = r * scale;                        // 0..scale across the photo, in bubble radii
                float hb = sqrt(saturate(1.0 - rb * rb));
                float3 surfaceVS = input.centerVS + float3(d * input.radius, (hb + _DepthBias) * bubble);
                float h = sqrt(saturate(1.0 - r * r));
                float4 surfaceCS = TransformWViewToHClip(surfaceVS);
                depth = surfaceCS.z / surfaceCS.w;

                float2 uv = input.uv * _Crop.xy + _Crop.zw;
                half3 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                // A little of the bubble's volume: lit from the upper left, darker at the rim.
                float3 n = float3(d, h);
                float light = 0.82 + 0.18 * saturate(dot(n, normalize(float3(-0.35, 0.45, 0.82))));
                color *= light * lerp(0.88, 1.0, h);
                half grey = dot(color, half3(0.299, 0.587, 0.114));
                color = lerp(grey.xxx, color, _Saturation);
                color *= _DimFactor;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}

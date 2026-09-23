// Stereo-correct replacement for the game's "Image Effects/Sonar" screen
// effect. The fragment is a register-faithful port of the 2018 3Dmigoto
// replacement of that same pass (3DMigoto hash 4e821607b4b7312e,
// byte-identical in the 2018/2021/2024 fix archives), with one change:
// the old line
//     float4 s = StereoParams.Load(0);
//     r0.y -= s.x * (r0.x - s.y) / unity_CameraProjection[0].x;
// (a driver-level StereoParams correction) is replaced by a runtime
// per-eye camera offset: the reconstructed world position is translated
// by the offset of the eye being rendered (+- stereoSeparation/2 along
// the camera right axis), so both eyes reconstruct the same world grid.
//
// Build: Unity 2019.4, asset bundle named "sonar_resources" (flat, next to
// amplify_resources in StreamingAssets) containing this shader, platform
// Windows 64. The mod looks the shader up by name "SubmersedVR/SonarScreenStereo".
Shader "SubmersedVR/SonarScreenStereo"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
        // _CameraDepthTexture / _CameraGBufferTexture2 are built-in: the
        // engine auto-binds them to the declared samplers; a material
        // property with a default would override that binding
        _EyeOffL ("Eye Offset (left half)", Vector) = (0, 0, 0, 0)
        _EyeOffR ("Eye Offset (right half)", Vector) = (0, 0, 0, 0)
        _UseNormalGate ("Use GBuffer2 Normal Gate", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            // Built-in camera depth texture: the engine auto-binds its content,
            // but the sampler itself must be declared here (UnityCG.cginc does
            // NOT declare it)
            sampler2D _CameraDepthTexture;
            sampler2D _CameraGBufferTexture2;

            // Set globally by the game (SonarScreenFX, pingDistanceShaderID)
            uniform float _SonarPingDistance;
            uniform float4 _EyeOffL;
            uniform float4 _EyeOffR;
            uniform float _UseNormalGate;

            struct AppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Vertex stage matching the game's original "Image Effects/Sonar"
            // shader exactly (source extracted from the game's shader asset):
            // full MVP transform + the blit mesh's own UVs (its input
            // channels are POSITION + TEXCOORD0, confirmed in the asset
            // metadata).
            v2f vert(AppData v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : COLOR
            {
                float4 r0, r1, r2, r3;

                r0.x = _ProjectionParams.y * _ProjectionParams.z;
                r0.yz = _ProjectionParams.zy + -_ProjectionParams.yz;
                r1.xyzw = tex2D(_CameraDepthTexture, i.uv).xyzw;
                r0.w = 1 + -r1.x;
                r0.yz = r0.ww * r0.yz + _ProjectionParams.yz;
                r0.x = r0.x / r0.z;

                float depth = r0.x;

                r0.y = r0.y + -r0.x;
                r0.x = unity_OrthoParams.w * r0.y + r0.x;
                r0.y = 1 + -r0.x;
                r0.y = unity_OrthoParams.w * r0.y + r0.x;
                r1.xy = i.uv * float2(2, 2) + float2(-1, -1);
                r1.zw = -unity_CameraProjection[2].xy + r1.xy;
                r2.x = unity_CameraProjection[0].x;
                r2.y = unity_CameraProjection[1].y;
                r1.zw = r1.zw / r2.xy;
                r2.xy = r1.xy / r2.xy;
                r0.yz = r1.zw * r0.yy;

                // Stereo fix: the per-eye depth was rendered from a camera
                // shifted by the eye offset (C# sets _EyeOffL/_EyeOffR from
                // SNCameraRoot.stereoSeparation); translate the reconstructed
                // world position by that offset. On a side-by-side frame the
                // offset is picked per half; on a per-eye call C# sets both
                // uniforms to the offset of the eye being rendered.
                float3 eyeOff = (i.uv.x < 0.5) ? _EyeOffL.xyz : _EyeOffR.xyz;

                r1.xyz = unity_CameraToWorld[1].xyz * r0.zzz;
                r1.xyz = unity_CameraToWorld[0].xyz * r0.yyy + r1.xyz;
                r0.xyz = unity_CameraToWorld[2].xyz * -r0.xxx + r1.xyz;
                r0.xyz = unity_CameraToWorld[3].xyz + r0.xyz;
                r0.xyz = r0.xyz + eyeOff;

                r0.xyz = _Time.yyy * float3(0.300000012, 0.300000012, 0.300000012) + r0.xyz;
                r0.xyz = float3(1.33333337, 1.33333337, 1.33333337) * r0.xyz;
                r0.xyz = frac(r0.xyz);
                r0.x = min(r0.x, r0.y);
                r0.x = min(r0.x, r0.z);
                r0.x = 1 + -r0.x;
                r0.x = log2(r0.x);
                r0.x = 7 * r0.x;
                r0.x = exp2(r0.x);
                r2.z = -1;
                r0.z = dot(r2.xyz, r2.xyz);
                r0.z = rsqrt(r0.z);
                r1.xyz = r2.xyz * r0.zzz;
                r0.z = dot(-r1.xyz, -r1.xyz);
                r0.z = rsqrt(r0.z);
                r1.xyz = -r1.xyz * r0.zzz;
                r2.xyzw = tex2D(_CameraGBufferTexture2, i.uv).xyzw;
                r2.xyz = float3(-0.5, -0.5, -0.5) + r2.xyz;
                r0.z = dot(r1.xyz, r2.xyz);
                r0.z = abs(r0.z) * abs(r0.z);
                r0.yz = float2(2, 6) * r0.xz;
                r0.z = min(1, r0.z);
                r0.z = lerp(1.0, r0.z, _UseNormalGate);
                r1.x = r0.y * r0.z;
                r0.y = -r0.z * 0.5 + 1;
                r0.z = 0.999899983 + -r0.w;
                r0.w = log2(r0.w);
                r0.w = 2000 * r0.w;
                r0.w = exp2(r0.w);
                r0.w = min(1, r0.w);
                r0.z = ceil(r0.z);
                r1.w = _SonarPingDistance * 2 + -r0.w;
                r2.x = 100 * r1.w;
                r1.w = saturate(r1.w * 4 + -2.5);
                r1.w = 1 + -r1.w;
                r2.x = saturate(r2.x);
                r2.x = r2.x * r1.w;
                r2.y = 0.899999976 + -r0.w;
                r2.x = r2.x * r2.y;
                r2.y = 1000 * r0.w;
                r2.y = min(1, r2.y);
                r2.x = r2.x * r2.y;
                r0.z = saturate(r2.x * r0.z);
                r0.xy = r0.xy * r0.zz;
                r0.y = r0.y * r0.w;
                r0.w = r0.w + r0.z;
                r0.w = min(1, r0.w);
                r2.xyz = r0.www * float3(0, -0.100000001, -0.800000012) + float3(0, 0.100000001, 0.800000012);
                r0.w = r1.w * r0.z;
                r0.w = -r0.w * 0.5 + 1;
                r1.w = r1.w * r0.w;
                r1.w = log2(r1.w);
                r1.w = 20 * r1.w;
                r1.w = exp2(r1.w);
                r1.w = r1.w * r0.z;
                r0.y = r1.w * 10 + r0.y;
                r2.xyz = r0.yyy * r2.xyz;
                r3.xyzw = tex2D(_MainTex, i.uv).xyzw;
                r0.yzw = r3.xyz * r0.www + r2.xyz;
                r1.yz = float2(0, 0);
                r1.xyz = r1.xyz + -r0.yzw;
                r3.xyz = r0.xxx * r1.xyz + r0.yzw;
                r3.w = r3.w;
                return r3;
            }
            ENDCG
        }
    }
}

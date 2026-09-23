// Screen-space sonar relief readout: TOPOGRAPHIC CONTOUR LINES + strict
// silhouettes, revealed by the ping front - EXACTLY like the blue wave
// (same per-eye unprojection, same front, same two-phase trail with the
// same settings sliders, same _WaveColor), but the reveal is masked to
// relief features instead of the whole surface.
//
// Detection (all from rendered buffers, so occlusion is free - only
// VISIBLE geometry is ever lit):
//   - topographic contours: thin world-space lines at fixed ALTITUDE
//     intervals (_EdgeInterval, default 5 m) - the "relief" readout, a
//     topographic map of the terrain. Pure worldPos function: no neighbor
//     samples, no thresholds to break.
//   - silhouettes: depth discontinuity (4 neighbors) measured in
//     LINEARIZED (world) distance and compared RELATIVE to the distance -
//     a 2-6 % depth jump is a real silhouette at any range. The old raw-
//     depth threshold (0.008) was far too sensitive at close range
//     (perspective depth is hugely expanded near the camera) and lit the
//     whole nearby terrain as a "monochrome surface" (in-game feedback
//     v57); the G-buffer normal gradient (0.35 +- 0.15) caught every rocky
//     slope and is dropped.
//   - cleared depth (sky, ~1.0) is masked out
// The mask is multiplied by the wave front + the same two-phase trail as
// the blue wave, then added additively in _WaveColor (lagoon, independent
// of ambient light).
//
// v57 fixes (still in): the glow color was a global `const float3 Lagoon`
// - proven broken in this bundle-compiled pipeline (v43-v52: consts render
// black, which is why the edges effect was inert) - now the real _WaveColor
// uniform like the wave. The unprojection used the CENTER camera matrices
// (inter-eye mismatch) - now the same per-eye matrices as the wave. The
// trail was the old single exponential - now the identical two-phase trail.
//
// _EdgeInterval / _EdgeStrength are set by C# (SonarScreenEdges).
//
// Build: Unity 2019.4, asset bundle named "sonar_resources" (flat, next to
// amplify_resources in StreamingAssets) containing this shader, platform
// Windows 64. The mod looks the shader up by name
// "SubmersedVR/SonarScreenEdges".
Shader "SubmersedVR/SonarScreenEdges"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
        // _CameraDepthTexture is built-in: the engine auto-binds its
        // content to the declared sampler; a material property with a
        // default would override that binding
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

            // Same pulse uniforms as the blue wave (set by SonarScreenEdges)
            uniform float3 _WaveOrigin;
            uniform float _WaveTime;     // seconds since ping start (<0 = off)
            uniform float _WaveSpeed;    // m/s (range / front duration)
            uniform float _WaveRange;    // m
            // Same two-phase trail as the blue wave (same settings sliders)
            uniform float _WaveTrailStart;   // 0..1, level right behind the front
            uniform float _WaveTrailPlateau; // s, phase 1 (start -> level)
            uniform float _WaveTrailLevel;   // 0..1, level at the plateau end
            uniform float _WaveTrailFade;    // s, phase 2 (level -> 0)
            uniform float _WaveTrailCurve;   // 0 = linear, 1 = ease-in 1-x^2
            // The glow color (lagoon, set by C#) - a real uniform: a global
            // const in this bundle-compiled pipeline does NOT hold its
            // source value (v43-v52, the edges glow used to render black)
            uniform float3 _WaveColor;
            // Same per-eye matrices as the blue wave (set by SonarScreenEdges)
            uniform float4x4 _EyeC2W;
            uniform float4 _EyeProjTerms;
            // Edge detection parameters
            uniform float2 _EdgeTexel;          // (1/RTwidth, 1/RTheight)
            uniform float _EdgeInterval;        // m, contour altitude interval
            uniform float _EdgeStrength;        // overall additive strength
            // v59: same as the wave: 0 = constant-speed front, 1 =
            // accelerated (quadratic) front
            uniform float _WaveEaseFront;
            // v59 debug visualization: 0 = normal (wave reveal), 1 =
            // contour lines ONLY (always-on, no wave reveal), 2 =
            // silhouettes ONLY (no reveal)
            uniform float _EdgeDebugVis;

            // Linearized view distance (m in front of the camera) from raw
            // depth: nf / (n + d(f-n)) with _ProjectionParams.y = near,
            // .z = far (the same linearization the unprojection above uses,
            // perspective case). Used by the silhouette test so the
            // threshold is in WORLD meters / relative, at any range
            float linViewDist(float d)
            {
                float n = _ProjectionParams.y;
                float f = _ProjectionParams.z;
                return (n * f) / (n + d * (f - n));
            }

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
                float4 r0, r1, r3;

                // Source image (the pass-through base)
                r3.xyzw = tex2D(_MainTex, i.uv).xyzw;

                // No active ping: clean pass-through (except in debug
                // mode, which draws the masks without the wave reveal)
                if (_WaveTime < 0.0 && _EdgeDebugVis < 0.5)
                {
                    return r3;
                }

                // --- screen -> world (PER-EYE matrices from C#, like the wave) ---
                r0.x = _ProjectionParams.y * _ProjectionParams.z;
                r0.yz = _ProjectionParams.zy + -_ProjectionParams.yz;
                r1.xyzw = tex2D(_CameraDepthTexture, i.uv).xyzw;
                r0.w = 1 + -r1.x;
                r0.yz = r0.ww * r0.yz + _ProjectionParams.yz;
                r0.x = r0.x / r0.z;
                r0.y = r0.y + -r0.x;
                r0.x = unity_OrthoParams.w * r0.y + r0.x;
                r0.y = 1 + -r0.x;
                r0.y = unity_OrthoParams.w * r0.y + r0.x;

                float2 pOffset = _EyeProjTerms.zw;
                float2 pScale = float2(_EyeProjTerms.x, _EyeProjTerms.y);
                r1.xy = i.uv * float2(2, 2) + float2(-1, -1);
                r1.zw = -pOffset + r1.xy;
                r1.zw = r1.zw / pScale;
                r0.yz = r1.zw * r0.yy;

                float3 viewPos = float3(r0.y, r0.z, -r0.x);
                float3 worldPos = mul(_EyeC2W, float4(viewPos, 1.0)).xyz;

                // Beyond the range: nothing to reveal (debug mode skips
                // the range check: _WaveOrigin may be (0,0,0) with no ping)
                float d = distance(worldPos, _WaveOrigin);
                if (d > _WaveRange && _EdgeDebugVis < 0.5)
                {
                    return r3;
                }

                // --- mask 1: topographic contour lines (the relief) ---
                // Thin world-space lines at fixed altitudes: every
                // _EdgeInterval meters of world Y. Pure worldPos function -
                // no neighbors, no thresholds. ~0.5 m wide lines.
                float cy = worldPos.y / _EdgeInterval;
                float cf = frac(cy + 0.5);
                float cdy = min(cf, 1.0 - cf) * _EdgeInterval;
                float contour = 1.0 - smoothstep(0.0, 0.5, cdy);

                // --- mask 2: strict silhouettes (depth discontinuity) ---
                // (zC is r1.x: the center depth sample from the
                // unprojection above). Linearized + RELATIVE comparison so
                // a 2-6 % depth jump counts at any range - the old raw
                // depth threshold was 100x too sensitive at close range
                float2 t = _EdgeTexel;
                float zC = r1.x;
                float lC = linViewDist(zC);
                float zL = tex2D(_CameraDepthTexture, i.uv - float2(t.x, 0.0)).x;
                float zR = tex2D(_CameraDepthTexture, i.uv + float2(t.x, 0.0)).x;
                float zD = tex2D(_CameraDepthTexture, i.uv - float2(0.0, t.y)).x;
                float zU = tex2D(_CameraDepthTexture, i.uv + float2(0.0, t.y)).x;
                float gd = max(max(abs(linViewDist(zR) - lC), abs(linViewDist(zL) - lC)),
                               max(abs(linViewDist(zU) - lC), abs(linViewDist(zD) - lC)));
                gd /= max(lC, 0.01);
                float edgeZ = smoothstep(0.02, 0.06, gd);

                float edge = max(contour, edgeZ);
                // Sky (cleared depth) has no geometry to outline
                float sky = step(zC, 0.999);
                edge *= sky;

                // v60 debug visualization: draw ONE mask only, ALWAYS-ON -
                // no ping, no wave reveal. This branch runs BEFORE the
                // reveal early-return, so the masks are visible even with
                // no ping (v59 bug: it was after the reveal check, so it
                // only worked mid-ping). Change the contour interval slider
                // here to test it
                if (_EdgeDebugVis > 0.5)
                {
                    float dbg = _EdgeDebugVis < 1.5 ? contour : edgeZ;
                    float g = dbg * sky * _EdgeStrength;
                    if (g > 0.001) r3.rgb = r3.rgb + _WaveColor * g;
                    return r3;
                }

                // --- pulse reveal (IDENTICAL front + two-phase trail to the blue wave) ---
                float frontR = _WaveTime * _WaveSpeed;
                float tArr = d / max(_WaveSpeed, 0.0001);
                // v59 accelerated front (legacy style): same quadratic as
                // the wave - frontR = v^2*t^2/range, tArr = sqrt(d*range)/v
                if (_WaveEaseFront > 0.5)
                {
                    frontR = _WaveSpeed * _WaveSpeed * _WaveTime * _WaveTime / max(_WaveRange, 0.0001);
                    tArr = sqrt(d * max(_WaveRange, 0.0001)) / max(_WaveSpeed, 0.0001);
                }
                float front = 1.0 - smoothstep(0.0, 2.0, abs(d - frontR));
                float trail = 0.0;
                float trailAge = _WaveTime - tArr;
                if (trailAge > 0.0)
                {
                    float t1 = max(_WaveTrailPlateau, 0.0);
                    float t2 = max(_WaveTrailFade, 0.0);
                    float lvl = saturate(_WaveTrailLevel);
                    float st = saturate(_WaveTrailStart);
                    if (trailAge < t1 + t2)
                    {
                        if (trailAge <= t1)
                        {
                            float x = t1 > 0.0 ? trailAge / t1 : 1.0;
                            float drop = _WaveTrailCurve > 0.5 ? x * x : x;
                            trail = st - (st - lvl) * drop;
                        }
                        else
                        {
                            float x = t2 > 0.0 ? (trailAge - t1) / t2 : 1.0;
                            float drop = _WaveTrailCurve > 0.5 ? x * x : x;
                            trail = lvl * (1.0 - drop);
                        }
                    }
                }
                float reveal = max(front, trail);
                if (reveal <= 0.001)
                {
                    return r3;
                }

                float glow = edge * reveal * _EdgeStrength;
                if (glow <= 0.001)
                {
                    return r3;
                }

                // Additive lagoon glow over the source (same color uniform as the wave)
                r3.rgb = r3.rgb + _WaveColor * glow;
                return r3;
            }
            ENDCG
        }
    }
}

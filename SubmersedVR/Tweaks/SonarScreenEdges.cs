using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Screen-space sonar relief readout (v58): TOPOGRAPHIC CONTOUR LINES
    // (world-space lines at fixed altitude intervals - the "relief" readout)
    // + strict silhouettes (linearized depth discontinuity, relative to the
    // distance so it works at any range). Lit up EXACTLY like the blue wave
    // (same per-eye unprojection, same front speed - range / sweep duration,
    // same two-phase trail with the same settings sliders, same _WaveColor)
    // - only the reveal is masked to the relief features. Occlusion comes
    // for free (only rendered geometry is read), so only VISIBLE features
    // appear - no geometry access, no extra draw calls, safe in multi-pass
    // stereo.
    //
    // The edge thresholds/strength are uniforms set here so they can be
    // tuned with a DLL-only zip (no bundle rebuild).
    static class SonarScreenEdges
    {
        // Overall additive strength (the contour interval comes from the
        // settings slider; the silhouette test is fixed in the shader -
        // 2-6 % linearized depth jump, the same at any range)
        private const float Strength = 1.0f;

        // The glow color (lagoon) - same value as the blue wave, sent as a
        // REAL uniform (a global const renders black in this pipeline)
        private static readonly Vector3 WaveColor = new Vector3(0f, 0.9f, 1f);

        static SonarScreenFX trackedFx;
        static Material originalMaterial;
        static Material edgesMaterial;
        static Shader edgesShader;
        static bool initFailed;
        static bool loadLogged;
        static bool swapLogged;
        // Per-frame call counter for the eye fallback (same mechanism as the
        // wave driver)
        static int lastFrame;
        static int callsThisFrame;
        static bool eyeFallbackLogged;
        // One "edges diag" log per ping (readback of the uniforms actually
        // handed to the shader)
        static bool edgesPingLogPending = true;

        static bool IsActive()
        {
            return Settings.SonarModEnabled && SonarEffectOptions.IsEdges(Settings.SonarScreenEffect);
        }

        // Called on selection change: if edges is no longer the selected
        // effect, restore the game's material (the next frame would do it,
        // but this keeps the state tidy for the other modules)
        public static void ApplyState()
        {
            if (IsActive())
            {
                return;
            }
            if (trackedFx != null && trackedFx._material == edgesMaterial)
            {
                trackedFx._material = originalMaterial;
            }
            if (edgesMaterial != null)
            {
                Object.Destroy(edgesMaterial);
                edgesMaterial = null;
            }
            originalMaterial = null;
            trackedFx = null;
            initFailed = false;
            swapLogged = false;
        }

        public static void OnQuit()
        {
            ApplyState();
            edgesShader = null;
        }

        // Called by the prefix patch before the game's own blit runs
        public static void BeforeOnRenderImage(SonarScreenFX fx, RenderTexture source, RenderTexture destination)
        {
            if (!IsActive() || Mod.quitting)
            {
                // Safety: if no longer active but still swapped, restore
                if (trackedFx != null && trackedFx._material == edgesMaterial)
                {
                    trackedFx._material = originalMaterial;
                }
                return;
            }
            if (!EnsureShader())
            {
                return;
            }
            var orig = fx._material;
            if (orig == null)
            {
                // First frame: the effect's own CheckResources creates its
                // material during this call; the swap starts next frame
                return;
            }
            if (fx._material != edgesMaterial)
            {
                if (edgesMaterial != null && fx._material != originalMaterial)
                {
                    // The game recreated its material (scene reload /
                    // re-enable): re-capture the new original
                    Object.Destroy(edgesMaterial);
                    edgesMaterial = null;
                    Mod.logger.LogInfo("[SonarFX] edges: game material changed, re-capturing original");
                }
                originalMaterial = orig;
                edgesMaterial = new Material(orig);
                edgesMaterial.name = "SubmersedVR Sonar Edges";
                edgesMaterial.shader = edgesShader;
                edgesMaterial.SetFloat("_EdgeStrength", Strength);
                edgesMaterial.SetFloat("_EdgeInterval", Settings.SonarEdgeInterval);
                edgesMaterial.SetVector("_WaveColor", WaveColor);
                if (!swapLogged)
                {
                    swapLogged = true;
                    Mod.logger.LogInfo($"[SonarFX] edges: screen sonar swapped (orig={orig.name})");
                }
                fx._material = edgesMaterial;
            }
            UpdateUniforms(edgesMaterial, source);
        }

        static bool EnsureShader()
        {
            if (edgesShader != null)
            {
                return true;
            }
            if (initFailed)
            {
                return false;
            }
            edgesShader = SonarBundle.GetShader(SonarEffectOptions.EdgesShaderName);
            if (edgesShader == null)
            {
                initFailed = true;
                Mod.logger.LogInfo("[SonarFX] edges: sonar edges shader not found in sonar_resources bundle - option inert, original effect kept");
                return false;
            }
            if (!loadLogged)
            {
                loadLogged = true;
                Mod.logger.LogInfo($"[SonarFX] edges: sonar edges shader loaded ({edgesShader.name})");
            }
            return true;
        }

        // Per-eye camera matrices for the screen->world unprojection: the
        // exact same capture as the blue wave (SonarScreenWave)
        static void UpdateEyeMatrices(Material m, RenderTexture source)
        {
            var cam = Camera.current;
            var root = SNCameraRoot.main;
            if (cam == null && root != null)
            {
                cam = root.mainCam;
            }
            if (cam == null)
            {
                return;
            }
            int f = Time.frameCount;
            if (f != lastFrame)
            {
                lastFrame = f;
                callsThisFrame = 0;
            }
            callsThisFrame++;

            // Packed side-by-side frame: no test configuration for the
            // per-eye capture on that topology; fall back to the center
            // matrices (the pre-v57 behavior)
            bool packed = source != null && source.width > source.height;
            if (packed)
            {
                m.SetMatrix("_EyeC2W", cam.transform.localToWorldMatrix);
                var pc = cam.projectionMatrix;
                m.SetVector("_EyeProjTerms", new Vector4(pc.m00, pc.m11, pc.m02, pc.m12));
                return;
            }

            Camera.StereoscopicEye eye;
            if (cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
            {
                eye = Camera.StereoscopicEye.Left;
            }
            else if (cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
            {
                eye = Camera.StereoscopicEye.Right;
            }
            else
            {
                if (!eyeFallbackLogged)
                {
                    eyeFallbackLogged = true;
                    Mod.logger.LogInfo($"[SonarFX] edges: stereoActiveEye={cam.stereoActiveEye} in OnRenderImage, using call order as eye source");
                }
                eye = callsThisFrame >= 2 ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            }

            // Baked sign fix (confirmed in-game for the grid): the game's
            // per-eye matrix convention points the opposite way from Unity's
            // GetStereoViewMatrix, so the OPPOSITE eye is used
            Camera.StereoscopicEye effEye = eye == Camera.StereoscopicEye.Left
                ? Camera.StereoscopicEye.Right
                : Camera.StereoscopicEye.Left;
            m.SetMatrix("_EyeC2W", cam.GetStereoViewMatrix(effEye).inverse);
            var p = cam.GetStereoProjectionMatrix(effEye);
            m.SetVector("_EyeProjTerms", new Vector4(p.m00, p.m11, p.m02, p.m12));
        }

        static void UpdateUniforms(Material m, RenderTexture source)
        {
            if (source != null)
            {
                m.SetVector("_EdgeTexel", new Vector2(1f / source.width, 1f / source.height));
            }
            UpdateEyeMatrices(m, source);
            // v59: set even before any ping - the debug visualization draws
            // the masks without the wave reveal (always-on)
            m.SetFloat("_EdgeDebugVis", Settings.SonarEdgeDebugVis);
            m.SetFloat("_WaveEaseFront", SonarScreenWave.FixedEaseFront);
            if (!SonarWorldPing.HasPinged)
            {
                // No ping yet: keep the effect off so the shader outputs a
                // clean pass-through
                m.SetFloat("_WaveTime", -1f);
                return;
            }
            // IDENTICAL pulse to the blue wave (v68: the wave's frozen
            // constants, shared - same front crossing time, same two-phase
            // trail)
            float range = SonarScreenWave.FixedRange;
            float speed = range / SonarScreenWave.FixedSweep;
            m.SetFloat("_EdgeInterval", Settings.SonarEdgeInterval);
            m.SetVector("_WaveOrigin", SonarWorldPing.LastOrigin);
            m.SetFloat("_WaveTime", Time.time - SonarWorldPing.LastPingTime);
            m.SetFloat("_WaveSpeed", speed);
            m.SetFloat("_WaveRange", range);
            m.SetFloat("_WaveTrailStart", SonarScreenWave.FixedTrailStart);
            m.SetFloat("_WaveTrailPlateau", SonarScreenWave.FixedTrailPlateau);
            m.SetFloat("_WaveTrailLevel", SonarScreenWave.FixedTrailLevel);
            m.SetFloat("_WaveTrailFade", SonarScreenWave.FixedTrailFade);
            m.SetFloat("_WaveTrailCurve", SonarScreenWave.FixedTrailCurve);

            // Per-ping diagnostic (v59): the exact uniforms handed to the
            // shader, READ BACK from the material (the real GPU values) -
            // proves the contour interval slider reaches the shader
            if (SonarWorldPing.IsPinging && edgesPingLogPending)
            {
                edgesPingLogPending = false;
                float wt = Time.time - SonarWorldPing.LastPingTime;
                Mod.logger.LogInfo($"[SonarFX] edges diag: _WaveTime={wt:F2} _EdgeInterval={m.GetFloat("_EdgeInterval"):F2} _EdgeStrength={m.GetFloat("_EdgeStrength"):F2} _EdgeDebugVis={m.GetFloat("_EdgeDebugVis"):0} easeFront={m.GetFloat("_WaveEaseFront"):0} _WaveRange={m.GetFloat("_WaveRange"):F0} _WaveSpeed={m.GetFloat("_WaveSpeed"):F1}");
            }
            if (!SonarWorldPing.IsPinging)
            {
                edgesPingLogPending = true;
            }
        }
    }

    // Swaps the effect's material before the game's own blit runs
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.OnRenderImage))]
    static class SonarScreenEdgesPatch
    {
        [HarmonyPrefix]
        static void Prefix(SonarScreenFX __instance, RenderTexture source, RenderTexture destination)
        {
            SonarScreenEdges.BeforeOnRenderImage(__instance, source, destination);
        }
    }
}

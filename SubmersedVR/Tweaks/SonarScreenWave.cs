using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Simple world-anchored sonar wave: a pale-blue front expands from the
    // ping origin (the vehicle position at ping time); every surface point
    // starts glowing when the front reaches it and keeps glowing for a few
    // seconds (persistence trail), revealing terrain relief through the
    // G-buffer world normals. No grid.
    //
    // Stateless: the effect is a pure function of (worldPos, t) - no
    // accumulation buffer, trivially safe in multi-pass stereo. The
    // screen->world unprojection uses the PER-EYE matrices (v55): the same
    // capture as the working stereo grid (GetStereoViewMatrix /
    // GetStereoProjectionMatrix, incl. the baked opposite-eye flip), so the
    // wave sits on the terrain in both eyes.
    static class SonarScreenWave
    {
        // Soft front band width in world meters. 10 m was too wide (in-game
        // feedback v54), 4 m a slim leading edge, v64: 1 m - a tight front
        // line (the user's preferred look)
        private const float BandWidth = 1f;

        // v68/v69: the user's FINAL wave parameters, ALL frozen (v69: the
        // range too, and with it the last sonar setting in the app - only
        // the screen-shader choice remains). Constants set ONCE at
        // material creation (zero per-frame cost, and no shader const -
        // the bundle-compiled const bug of v43-v52). The front speed
        // (range / sweep) and the fade start (range / 3.5) derive from the
        // range. Shared with the edges driver (same pulse)
        internal const float FixedRange = 350f;          // v69: max range, frozen
        internal const float FixedSweep = 2.5f;          // front crossing time
        internal const float FixedEaseFront = 1f;        // accelerated front on
        internal const float FixedTrailStart = 0.30f;    // trail level right behind the front
        internal const float FixedTrailPlateau = 2.0f;   // s, phase 1
        internal const float FixedTrailLevel = 0.2f;     // level at the plateau end
        internal const float FixedTrailFade = 0.5f;      // s, phase 2 (level -> 0)
        internal const float FixedTrailCurve = 0f;       // 0 = linear (eased off)
        internal const float FixedReliefMin = 0.75f;     // relief shading floor
        internal const float FixedRadar = 0f;            // 0 = classic mode (radar off)
        internal const float FixedRadarCurve = 1f;       // unused while classic
        internal const float FixedAttenEnd = 0f;         // fully transparent at max range
        internal const float FixedAttenCurve = 0.35f;    // fade curve exponent
        internal const float AttenStartDivisor = 3.5f;   // fade start = range / 3.5

        // The wave color (lagoon), sent to the shader as a REAL uniform.
        // No global const in the shader: v43-v52 proved a global const does
        // not hold its source value in this bundle-compiled pipeline.
        private static readonly Vector3 WaveColor = new Vector3(0f, 0.9f, 1f);

        static SonarScreenFX trackedFx;
        static Material originalMaterial;
        static Material waveMaterial;
        static Shader waveShader;
        static bool initFailed;
        static bool loadLogged;
        static bool swapLogged;
        // Per-frame call counter for the eye fallback (same mechanism as the
        // grid driver: first call of the frame = left eye, second = right)
        static int lastFrame;
        static int callsThisFrame;
        static bool eyeFallbackLogged;
        static bool wavePingLogPending = true;
        // Logs _WaveTime once per whole second of the ping (0,1,2,3,4) to see
        // whether it actually advances (a stuck _WaveTime=0 would keep the wave
        // a 2 m disc at the camera -> invisible -> "black screen")
        static int lastWaveTimeInt = -1;

        static bool IsActive()
        {
            return Settings.SonarModEnabled && SonarEffectOptions.IsWave(Settings.SonarScreenEffect);
        }

        // Called on selection change: if the wave is no longer the selected
        // effect, restore the game's material (the next frame would do it,
        // but this keeps the state tidy for the other module)
        public static void ApplyState()
        {
            if (IsActive())
            {
                return;
            }
            if (trackedFx != null && trackedFx._material == waveMaterial)
            {
                trackedFx._material = originalMaterial;
            }
            if (waveMaterial != null)
            {
                Object.Destroy(waveMaterial);
                waveMaterial = null;
            }
            originalMaterial = null;
            trackedFx = null;
            initFailed = false;
            swapLogged = false;
        }

        public static void OnQuit()
        {
            ApplyState();
            waveShader = null;
        }

        // Called by the prefix patch before the game's own blit runs
        public static void BeforeOnRenderImage(SonarScreenFX fx, RenderTexture source, RenderTexture destination)
        {
            if (!IsActive() || Mod.quitting)
            {
                // Safety: if no longer active but still swapped, restore
                if (trackedFx != null && trackedFx._material == waveMaterial)
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
            if (fx._material != waveMaterial)
            {
                if (waveMaterial != null && fx._material != originalMaterial)
                {
                    // The game recreated its material (scene reload /
                    // re-enable): re-capture the new original
                    Object.Destroy(waveMaterial);
                    waveMaterial = null;
                    Mod.logger.LogInfo("[SonarFX] wave: game material changed, re-capturing original");
                }
                originalMaterial = orig;
                waveMaterial = new Material(orig);
                waveMaterial.name = "SubmersedVR Sonar Wave";
                waveMaterial.shader = waveShader;
                waveMaterial.SetFloat("_WaveBand", BandWidth);
                waveMaterial.SetVector("_WaveColor", WaveColor);
                // v68/v69 frozen parameters (the user's final values), set
                // ONCE here: zero per-frame cost. Per frame only
                // _WaveTime, _WaveOrigin and the eye matrices change
                waveMaterial.SetFloat("_WaveTrailStart", FixedTrailStart);
                waveMaterial.SetFloat("_WaveTrailPlateau", FixedTrailPlateau);
                waveMaterial.SetFloat("_WaveTrailLevel", FixedTrailLevel);
                waveMaterial.SetFloat("_WaveTrailFade", FixedTrailFade);
                waveMaterial.SetFloat("_WaveTrailCurve", FixedTrailCurve);
                waveMaterial.SetFloat("_WaveEaseFront", FixedEaseFront);
                waveMaterial.SetFloat("_WaveReliefMin", FixedReliefMin);
                waveMaterial.SetFloat("_WaveRadar", FixedRadar);
                waveMaterial.SetFloat("_WaveRadarCurve", FixedRadarCurve);
                waveMaterial.SetFloat("_WaveAttenEnd", FixedAttenEnd);
                waveMaterial.SetFloat("_WaveAttenCurve", FixedAttenCurve);
                // v69: the range is frozen too - speed and fade start are
                // constants as well, so per-frame only _WaveTime,
                // _WaveOrigin and the eye matrices change
                waveMaterial.SetFloat("_WaveRange", FixedRange);
                waveMaterial.SetFloat("_WaveSpeed", FixedRange / FixedSweep);
                waveMaterial.SetFloat("_WaveAttenStart", FixedRange / AttenStartDivisor);
                if (!swapLogged)
                {
                    swapLogged = true;
                    Mod.logger.LogInfo($"[SonarFX] wave: screen sonar swapped (orig={orig.name})");
                }
                fx._material = waveMaterial;
            }
            UpdateUniforms(waveMaterial, source);
        }

        static bool EnsureShader()
        {
            if (waveShader != null)
            {
                return true;
            }
            if (initFailed)
            {
                return false;
            }
            waveShader = SonarBundle.GetShader(SonarEffectOptions.WaveShaderName);
            if (waveShader == null)
            {
                initFailed = true;
                Mod.logger.LogInfo("[SonarFX] wave: sonar wave shader not found in sonar_resources bundle - option inert, original effect kept");
                return false;
            }
            if (!loadLogged)
            {
                loadLogged = true;
                Mod.logger.LogInfo($"[SonarFX] wave: sonar wave shader loaded ({waveShader.name})");
            }
            return true;
        }

        // Per-eye camera matrices for the screen->world unprojection (v55):
        // the same capture as the working stereo grid
        // (SonarScreenShaderFixV2). The built-in matrices in this pass are
        // the CENTER eye's; the depth texture is per-eye, so the unprojection
        // must use the per-eye ones or the wave front lands at a different
        // apparent distance in each eye.
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

            // Packed side-by-side frame: the per-eye matrix capture has no
            // test configuration for that topology; fall back to the center
            // matrices (the v54 behavior) rather than a half-guessed eye
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
                    Mod.logger.LogInfo($"[SonarFX] wave: stereoActiveEye={cam.stereoActiveEye} in OnRenderImage, using call order as eye source");
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
            UpdateEyeMatrices(m, source);
            if (!SonarWorldPing.HasPinged)
            {
                // No ping yet: keep the wave off so the shader outputs a
                // clean pass-through
                m.SetFloat("_WaveTime", -1f);
                return;
            }
            // v69: EVERYTHING is frozen (set once at material creation) -
            // per frame only the ping timing and origin change
            m.SetVector("_WaveOrigin", SonarWorldPing.LastOrigin);
            m.SetFloat("_WaveTime", Time.time - SonarWorldPing.LastPingTime);

            // Per-ping diagnostic: the exact uniforms handed to the shader
            // (READ BACK from the material, so it's the real GPU value), incl.
            // _WaveBand which is set only once at material creation
            if (SonarWorldPing.IsPinging && wavePingLogPending)
            {
                wavePingLogPending = false;
                float wt = Time.time - SonarWorldPing.LastPingTime;
                Mod.logger.LogInfo($"[SonarFX] wave diag: _WaveTime={wt:F2} _WaveOrigin={SonarWorldPing.LastOrigin} _WaveSpeed={m.GetFloat("_WaveSpeed"):F1} _WaveRange={m.GetFloat("_WaveRange"):F0} trail=({m.GetFloat("_WaveTrailStart"):F2},{m.GetFloat("_WaveTrailPlateau"):F1}s,{m.GetFloat("_WaveTrailLevel"):F2},{m.GetFloat("_WaveTrailFade"):F1}s,curve={m.GetFloat("_WaveTrailCurve"):0}) easeFront={m.GetFloat("_WaveEaseFront"):0} reliefMin={m.GetFloat("_WaveReliefMin"):F2} radar={m.GetFloat("_WaveRadar"):0} radarCurve={m.GetFloat("_WaveRadarCurve"):F2} fade=({m.GetFloat("_WaveAttenStart"):F0}m,{m.GetFloat("_WaveAttenEnd"):F2},p={m.GetFloat("_WaveAttenCurve"):F1}) _WaveBand={m.GetFloat("_WaveBand"):F2}");
            }
            if (!SonarWorldPing.IsPinging)
            {
                wavePingLogPending = true;
                lastWaveTimeInt = -1;
            }
            // Per-second progression: log _WaveTime once per whole second so we
            // can see if it advances (0->1->2->3->4) or is stuck at 0
            if (SonarWorldPing.IsPinging)
            {
                float wt2 = Time.time - SonarWorldPing.LastPingTime;
                int wti = (int)wt2;
                if (wti != lastWaveTimeInt)
                {
                    lastWaveTimeInt = wti;
                    Mod.logger.LogInfo($"[SonarFX] wave time tick: _WaveTime={wt2:F2} (s={wti})");
                }
            }
        }
    }

    // Swaps the effect's material before the game's own blit runs
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.OnRenderImage))]
    static class SonarScreenWavePatch
    {
        [HarmonyPrefix]
        static void Prefix(SonarScreenFX __instance, RenderTexture source, RenderTexture destination)
        {
            SonarScreenWave.BeforeOnRenderImage(__instance, source, destination);
        }
    }
}

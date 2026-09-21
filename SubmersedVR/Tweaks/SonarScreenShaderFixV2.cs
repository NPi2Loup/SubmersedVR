using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // v32.1 - multi-mode stereo fix for the on-screen sonar grid.
    //
    // Diagnosis: the game's "Image Effects/Sonar" screen effect reconstructs
    // every pixel's world position from the per-eye depth buffer with the
    // CENTER camera matrices, so the grid's vanishing point ends up between
    // the two eyes.
    //
    // v32 assumed the per-eye error reduces to a constant world offset
    // (+- stereoSeparation/2 along the camera right). That is only true if
    // the per-eye projection matches the center one; VR frustums are
    // usually asymmetric, so the residual direction error grows with depth.
    // v32.1 therefore ships three selectable modes (the depth
    // linearization is kept exactly as the game's in all of them - near/
    // far are shared between the eyes):
    //
    //   0 "Translation (prototype)"   = the v32 fix (control)
    //   1 "Per-eye C2W only"          = center projection + per-eye pose
    //                                   (GetStereoViewMatrix(eye).inverse)
    //                                   - isolates the pose error
    //   2 "Per-eye matrices (robust)" = per-eye projection terms
    //                                   (p00, p11, p02, p12 of
    //                                   GetStereoProjectionMatrix) +
    //                                   per-eye pose - full correction
    //
    // The driver also serves the "Original (recompiled by us)" control
    // selection: the legacy shader (verbatim port of the game's effect,
    // zero eye offsets) swapped in the same way - identical launch path,
    // so it A/B-tests our launch against the game's original shader.
    //
    // On every ping the module logs the center/per-eye projection terms
    // and the eye world positions: that gives the numerical verdict
    // (frustum asymmetry, which eye the center matches, IPD coherence)
    // before any visual interpretation.
    //
    // Bundle lifecycle: loaded once, kept for the whole session (safe A/B
    // comparisons), unloaded only on application quit.
    static class SonarScreenShaderFixV2
    {
        static SonarScreenFX trackedFx;
        static Material originalMaterial;
        static Material stereoMaterial;
        static Shader sonarShader;
        static bool v2InitFailed;
        static bool reworkInitFailed;
        static bool legacyInitFailed;
        static bool swapLogged;
        static bool pingLogPending = true;
        static bool sbSFallbackLogged;
        static bool eyeFallbackLogged;
        static int lastFrame;
        static int callsThisFrame;
        static int pingPassCount;

        static bool IsActive()
        {
            return Settings.SonarModEnabled
                && (SonarEffectOptions.IsFix(Settings.SonarScreenEffect) || SonarEffectOptions.IsRecompiled(Settings.SonarScreenEffect));
        }

        // Called on selection change: if the stereo fix is no longer the
        // selected effect, restore the game's material (the next frame would
        // do it, but this keeps the state tidy for the other module)
        public static void ApplyState()
        {
            if (IsActive())
            {
                return;
            }
            if (trackedFx != null && trackedFx._material == stereoMaterial)
            {
                trackedFx._material = originalMaterial;
            }
            if (stereoMaterial != null)
            {
                Object.Destroy(stereoMaterial);
                stereoMaterial = null;
            }
            originalMaterial = null;
            trackedFx = null;
            v2InitFailed = false;
            reworkInitFailed = false;
            legacyInitFailed = false;
            swapLogged = false;
            sbSFallbackLogged = false;
            pingLogPending = true;
        }

        // Quit: restore + material destroy (the shared bundle is unloaded by
        // the SonarScreenDriver)
        public static void OnQuit()
        {
            ApplyState();
            sonarShader = null;
        }

        // Called by the prefix patch before the game's own blit runs
        public static void BeforeOnRenderImage(SonarScreenFX fx, RenderTexture source, RenderTexture destination)
        {
            if (!IsActive() || Mod.quitting)
            {
                // Safety: if no longer active but still swapped, restore
                if (trackedFx != null && trackedFx._material == stereoMaterial)
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
            if (fx._material != stereoMaterial)
            {
                if (stereoMaterial != null && fx._material != originalMaterial)
                {
                    // The game recreated its material (scene reload /
                    // re-enable): re-capture the new original
                    Object.Destroy(stereoMaterial);
                    stereoMaterial = null;
                    Mod.logger.LogInfo("[SonarFX] v2: game material changed, re-capturing original");
                }
                originalMaterial = orig;
                // Clone the game's material (keywords, render queue,
                // properties) then swap the shader
                bool recompiled = SonarEffectOptions.IsRecompiled(Settings.SonarScreenEffect);
                bool rework = SonarEffectOptions.IsRework(Settings.SonarScreenEffect);
                stereoMaterial = new Material(orig);
                stereoMaterial.name = recompiled ? "SubmersedVR Sonar Recompiled" : (rework ? "SubmersedVR Sonar Stereo V2 (rework)" : "SubmersedVR Sonar Stereo V2");
                stereoMaterial.shader = sonarShader;
                if (!recompiled)
                {
                    stereoMaterial.SetMatrix("_EyeC2W", Matrix4x4.identity);
                }
                if (!swapLogged)
                {
                    swapLogged = true;
                    string what = recompiled
                        ? "recompiled original (control)"
                        : (rework ? $"rework, mode={SonarEffectOptions.FixMode(Settings.SonarScreenEffect):0}" : $"mode={SonarEffectOptions.FixMode(Settings.SonarScreenEffect):0}");
                    Mod.logger.LogInfo($"[SonarFX] v2: screen sonar swapped (orig={orig.name}, {what})");
                }
                fx._material = stereoMaterial;
            }
            else if (stereoMaterial.shader != sonarShader)
            {
                // The selection switched between two driver-active options
                // with DIFFERENT shaders (e.g. "per-eye matrices" <->
                // "(rework)", or <-> "recompiled"): the material is still
                // ours, only its shader is stale. Swap it in place - the V2
                // shader family shares the property set, and Unity keeps the
                // material's property values across a shader change.
                bool recompiled = SonarEffectOptions.IsRecompiled(Settings.SonarScreenEffect);
                bool rework = SonarEffectOptions.IsRework(Settings.SonarScreenEffect);
                stereoMaterial.shader = sonarShader;
                stereoMaterial.name = recompiled ? "SubmersedVR Sonar Recompiled" : (rework ? "SubmersedVR Sonar Stereo V2 (rework)" : "SubmersedVR Sonar Stereo V2");
                if (!recompiled)
                {
                    stereoMaterial.SetMatrix("_EyeC2W", Matrix4x4.identity);
                }
                Mod.logger.LogInfo($"[SonarFX] v2: re-swapped in place (shader={sonarShader.name}, mode={SonarEffectOptions.FixMode(Settings.SonarScreenEffect):0})");
            }
            UpdateUniforms(stereoMaterial, source);
        }

        static bool EnsureShader()
        {
            bool recompiled = SonarEffectOptions.IsRecompiled(Settings.SonarScreenEffect);
            bool rework = SonarEffectOptions.IsRework(Settings.SonarScreenEffect);
            string wanted = recompiled ? SonarEffectOptions.LegacyShaderName : (rework ? SonarEffectOptions.V2ReworkShaderName : SonarEffectOptions.V2ShaderName);
            if (sonarShader != null && sonarShader.name == wanted)
            {
                return true;
            }
            if (recompiled ? legacyInitFailed : (rework ? reworkInitFailed : v2InitFailed))
            {
                return false;
            }
            sonarShader = SonarBundle.GetShader(wanted);
            if (sonarShader == null)
            {
                if (recompiled)
                {
                    legacyInitFailed = true;
                }
                else if (rework)
                {
                    reworkInitFailed = true;
                }
                else
                {
                    v2InitFailed = true;
                }
                Mod.logger.LogInfo($"[SonarFX] screen sonar shader not found in sonar_resources bundle ({wanted}) - option inert, original effect kept");
                return false;
            }
            Mod.logger.LogInfo($"[SonarFX] screen sonar shader loaded ({sonarShader.name})");
            return true;
        }

        static float ModeFromSettings()
        {
            return SonarEffectOptions.FixMode(Settings.SonarScreenEffect);
        }

        static void ResolveEye(Camera cam, out Camera.StereoscopicEye eye, out string source)
        {
            if (cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
            {
                eye = Camera.StereoscopicEye.Left;
                source = "activeEye";
                return;
            }
            if (cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
            {
                eye = Camera.StereoscopicEye.Right;
                source = "activeEye";
                return;
            }
            // Mono/unknown at this pipeline point: fall back to the call
            // order (first call of the frame = left eye, second = right,
            // as the v31 erase logic relies on)
            if (!eyeFallbackLogged)
            {
                eyeFallbackLogged = true;
                Mod.logger.LogInfo($"[SonarFX] v2: stereoActiveEye={cam.stereoActiveEye} in OnRenderImage, using call order as eye source");
            }
            eye = callsThisFrame >= 2 ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            source = "callOrder";
        }

        static void UpdateUniforms(Material m, RenderTexture source)
        {
            if (SonarEffectOptions.IsRecompiled(Settings.SonarScreenEffect))
            {
                // Control shader: the game's original effect, verbatim - no
                // per-eye correction, the eye offsets must stay zero
                m.SetVector("_EyeOffL", Vector3.zero);
                m.SetVector("_EyeOffR", Vector3.zero);
                if (SonarWorldPing.IsPinging && pingLogPending)
                {
                    pingLogPending = false;
                    Mod.logger.LogInfo($"[SonarFX] recompiled control active: rt={source.width}x{source.height} (game original, our launch path)");
                }
                if (!SonarWorldPing.IsPinging)
                {
                    pingLogPending = true;
                }
                return;
            }
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
            float sep = root != null ? root.stereoSeparation : 0f;

            int f = Time.frameCount;
            if (f != lastFrame)
            {
                lastFrame = f;
                callsThisFrame = 0;
            }
            callsThisFrame++;

            float mode = ModeFromSettings();
            // A per-eye post call gets a portrait RT; a packed
            // side-by-side frame is wider than tall. The per-eye matrix
            // modes have no test configuration for the packed topology,
            // so they degrade to translation there (documented
            // limitation, not a mathematical one)
            bool packed = source != null && source.width > source.height;
            if (mode > 0.5f && packed)
            {
                if (!sbSFallbackLogged)
                {
                    sbSFallbackLogged = true;
                    Mod.logger.LogInfo("[SonarFX] v2: packed stereo RT detected, per-eye matrix mode unavailable for this topology, falling back to translation");
                }
                mode = 0f;
            }

            Camera.StereoscopicEye eye;
            string eyeSrc;
            ResolveEye(cam, out eye, out eyeSrc);

            m.SetFloat("_FixMode", mode);
            m.SetFloat("_FixDebug", Settings.SonarDebugVis);
            m.SetFloat("_DebugEye", eye == Camera.StereoscopicEye.Left ? 0f : 1f);

            Vector3 half = cam.transform.right * (sep * 0.5f);
            // Baked sign fix (logs v37/v38/v39, confirmed in-game): the game's
            // own per-eye matrices (SNCameraRoot.matrixLeftEye/RightEye) point
            // the opposite way from Unity's GetStereoViewMatrix convention, so
            // every per-eye correction uses the OPPOSITE eye. flip=True was
            // confirmed correct (left eye aligned), so it's now always applied
            // (no longer a setting).
            Camera.StereoscopicEye effEye = eye == Camera.StereoscopicEye.Left
                ? Camera.StereoscopicEye.Right
                : Camera.StereoscopicEye.Left;
            Vector3 offApplied = Vector3.zero;
            if (mode < 0.5f)
            {
                // Translation mode (v32 behavior)
                Vector3 offL = -half;
                Vector3 offR = half;
                if (!packed)
                {
                    // Multi-pass: one full RT per eye. The offset must follow
                    // the RESOLVED eye (stereoActiveEye), not the call order:
                    // the pass order is not guaranteed left-first (log v35:
                    // call=1 was the right eye), so call-order sign selection
                    // applied the wrong eye's offset (error doubled)
                    offL = eye == Camera.StereoscopicEye.Left ? half : -half;
                    offR = offL;
                    offApplied = offL;
                }
                m.SetVector("_EyeOffL", offL);
                m.SetVector("_EyeOffR", offR);
            }
            else
            {
                var eyeC2W = cam.GetStereoViewMatrix(effEye).inverse;
                m.SetMatrix("_EyeC2W", eyeC2W);
                if (mode > 1.5f)
                {
                    var p = cam.GetStereoProjectionMatrix(effEye);
                    m.SetVector("_EyeProjTerms", new Vector4(p.m00, p.m11, p.m02, p.m12));
                }
            }

            // Per-pass eye diagnostic (v39): log the RAW stereoActiveEye + the
            // resolved eye for the first two passes of a ping. The v38 "left eye
            // aligned / right eye dark" result suggests the two OnRenderImage
            // passes may both resolve to the same eye (stereoActiveEye not
            // distinguishing them). This confirms or rules that out.
            if (SonarWorldPing.IsPinging)
            {
                if (pingPassCount < 2)
                {
                    pingPassCount++;
                    var appliedC2Wpos = (Vector3)cam.GetStereoViewMatrix(effEye).inverse.GetColumn(3);
                    Mod.logger.LogInfo($"[SonarFX] v2 pass: rawActive={cam.stereoActiveEye} resolvedEye={eye} eyeSrc={eyeSrc} call={callsThisFrame} effEye={(int)effEye} camProjM02={cam.projectionMatrix.m02:F4} appliedC2W=({appliedC2Wpos.x:F3},{appliedC2Wpos.y:F3},{appliedC2Wpos.z:F3}) ts={Time.timeScale:F1}");
                }
            }
            else
            {
                pingPassCount = 0;
            }

            if (SonarWorldPing.IsPinging && pingLogPending)
            {
                pingLogPending = false;
                LogDiag(cam, source, mode, eye, eyeSrc, callsThisFrame, sep, packed, offApplied);
            }
            if (!SonarWorldPing.IsPinging)
            {
                pingLogPending = true;
            }
        }

        // Per-ping numerical verdict: center vs per-eye projection terms,
        // eye world positions, IPD coherence, per-eye convergence angle
        static void LogDiag(Camera cam, RenderTexture source, float mode, Camera.StereoscopicEye eye, string eyeSrc, int call, float sep, bool packed, Vector3 offApplied)
        {
            var pC = cam.projectionMatrix;
            var pL = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            var pR = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            Matrix4x4 vL = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            Matrix4x4 vR = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
            var eL = (Vector3)vL.inverse.GetColumn(3);
            var eR = (Vector3)vR.inverse.GetColumn(3);
            Vector3 fwdL = -vL.inverse.GetColumn(2);
            Vector3 fwdR = -vR.inverse.GetColumn(2);
            Vector3 fwdC = cam.transform.forward;
            Vector3 dLR = eR - eL;
            float align = dLR.sqrMagnitude > 0.0001f ? Vector3.Dot(dLR.normalized, cam.transform.right) : 0f;
            Mod.logger.LogInfo($"[SonarFX] v2 diag: mode={mode:0} eyeSrc={eyeSrc} active={cam.stereoActiveEye} eye={eye} flip=1 call={call} conv={cam.stereoConvergence} sep={sep:F3} rt={source.width}x{source.height} packed={packed} ts={Time.timeScale:F1}");
            Mod.logger.LogInfo($"[SonarFX] v2 diag: pC=[{pC.m00:F4},{pC.m02:F4},{pC.m12:F4}] pL=[{pL.m00:F4},{pL.m02:F4},{pL.m12:F4}] pR=[{pR.m00:F4},{pR.m02:F4},{pR.m12:F4}]");
            Mod.logger.LogInfo($"[SonarFX] v2 diag: eL=({eL.x:F3},{eL.y:F3},{eL.z:F3}) eR=({eR.x:F3},{eR.y:F3},{eR.z:F3}) dLR={dLR.magnitude:F3} rightAlign={align:F2} offApplied=({offApplied.x:F3},{offApplied.y:F3},{offApplied.z:F3})");
            Mod.logger.LogInfo($"[SonarFX] v2 diag: angL={Vector3.Angle(fwdL, fwdC):F3} angR={Vector3.Angle(fwdR, fwdC):F3} deg (0=pure translation, >0=convergence)");
            var camPos = cam.transform.position;
            var mid = (eL + eR) * 0.5f;
            Mod.logger.LogInfo($"[SonarFX] v2 diag: camPos=({camPos.x:F3},{camPos.y:F3},{camPos.z:F3}) mid=({mid.x:F3},{mid.y:F3},{mid.z:F3}) (camPos~mid=central transform, camPos~eL/eR=per-eye transform)");
        }
    }

    // Swaps the effect's material before the game's own blit runs
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.OnRenderImage))]
    static class SonarScreenShaderFixV2Patch
    {
        [HarmonyPrefix]
        static void Prefix(SonarScreenFX __instance, RenderTexture source, RenderTexture destination)
        {
            SonarScreenShaderFixV2.BeforeOnRenderImage(__instance, source, destination);
        }
    }

    // Restores both screen-effect modules' materials and unloads the shared
    // bundle on quit
    class SonarScreenDriver : MonoBehaviour
    {
        void OnApplicationQuit()
        {
            SonarScreenShaderFixV2.OnQuit();
            SonarScreenWave.OnQuit();
            SonarScreenEdges.OnQuit();
            SonarBundle.OnQuit();
        }
    }

    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class AttachSonarScreenDriver
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            __instance.gameObject.GetOrAddComponent<SonarScreenDriver>();
            SonarEffectOptions.OnStartup();
        }
    }
}

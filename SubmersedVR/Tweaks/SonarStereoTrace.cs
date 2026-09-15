using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Diagnostic (temporary): log the stereo rendering state and the sonar ping
    // call chain while a sonar ping is active, to find what makes the scan wave
    // render differently in each eye in stereo VR.
    // UnityEngine engine methods (Shader.SetGlobal*, Material.Set*) are native
    // with no managed body and cannot be patched with Harmony, so this trace
    // only patches game assembly methods.
    static class SonarStereoTrace
    {
        private const float WindowDuration = 3f;

        static float windowEnd = -1f;
        static bool windowActive;

        public static bool WindowOpen => windowActive && Time.time <= windowEnd;

        public static void OpenWindow(string source)
        {
            if (!WindowOpen)
            {
                windowEnd = Time.time + WindowDuration;
                windowActive = true;
                SonarTraceEyeLogger.ResetSeen();
                Mod.logger.LogInfo($"[SonarTrace] ping ({source}) - log window open for {WindowDuration}s");
                var root = SNCameraRoot.main;
                if (root != null)
                {
                    Mod.logger.LogInfo($"[SonarTrace] stereoSeparation={root.stereoSeparation}");
                    Mod.logger.LogInfo($"[SonarTrace] matrixLeftEye={root.matrixLeftEye}");
                    Mod.logger.LogInfo($"[SonarTrace] matrixRightEye={root.matrixRightEye}");
                }
            }
        }

        public static void Log(string msg)
        {
            if (WindowOpen)
            {
                Mod.logger.LogInfo($"[SonarTrace] {msg}");
            }
        }
    }

    // Logs the render camera position once per stereo eye during the trace window
    class SonarTraceEyeLogger : MonoBehaviour
    {
        static HashSet<string> seen = new HashSet<string>();

        public static void ResetSeen()
        {
            seen.Clear();
        }

        void OnPreRender()
        {
            if (!SonarStereoTrace.WindowOpen || Camera.current == null) return;
            string eye = Camera.current.stereoTargetEye.ToString();
            if (seen.Add(eye))
            {
                SonarStereoTrace.Log($"render camera {eye} pos={transform.position} stereoSep={Camera.current.stereoSeparation}");
            }
        }
    }

    #region Patches

    // Attach the eye position logger to the main camera
    [HarmonyPatch(typeof(SNCameraRoot), nameof(SNCameraRoot.Awake))]
    static class SonarTraceAttach
    {
        [HarmonyPostfix]
        static void Postfix(SNCameraRoot __instance)
        {
            var cam = __instance.mainCam;
            if (cam != null)
            {
                cam.gameObject.GetOrAddComponent<SonarTraceEyeLogger>();
            }
            Mod.logger.LogInfo("[SonarTrace] armed");
        }
    }

    // The sonar ping visual (shared entry point for Seamoth and Cyclops)
    [HarmonyPatch(typeof(SNCameraRoot), nameof(SNCameraRoot.SonarPing))]
    static class SonarTracePing
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            SonarStereoTrace.OpenWindow("SNCameraRoot.SonarPing");
        }
    }

    // Cyclops sonar button (extra marker)
    [HarmonyPatch(typeof(CyclopsSonarButton), nameof(CyclopsSonarButton.SonarPing))]
    static class SonarTraceCyclopsPing
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            SonarStereoTrace.OpenWindow("CyclopsSonarButton.SonarPing");
        }
    }

    // World scan sweep VFX
    [HarmonyPatch(typeof(VFXScan), nameof(VFXScan.StartScan))]
    static class SonarTraceVFXScan
    {
        [HarmonyPostfix]
        static void Postfix(VFXScan __instance)
        {
            SonarStereoTrace.Log($"VFXScan.StartScan: active={__instance.scanActive} duration={__instance.scanDuration} renderers={__instance.renderers?.Length}");
        }
    }

    // Per-camera CommandBuffer scan VFX (one buffer per eye in stereo)
    [HarmonyPatch(typeof(VFXScanning), nameof(VFXScanning.StartScan))]
    static class SonarTraceVFXScanning
    {
        [HarmonyPostfix]
        static void Postfix(VFXScanning __instance, Material mat)
        {
            string cameras = "none";
            if (__instance.m_Cameras != null && __instance.m_Cameras.Count > 0)
            {
                var names = new List<string>();
                foreach (var camera in __instance.m_Cameras.Keys)
                {
                    names.Add(camera.name);
                }
                cameras = string.Join(",", names);
            }
            SonarStereoTrace.Log($"VFXScanning.StartScan: material={mat?.name} renderers={__instance.renderersToScan?.Count} cameras=[{cameras}]");
        }
    }

    // Screen-space sonar ping FX
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.Ping))]
    static class SonarTraceScreenFX
    {
        [HarmonyPostfix]
        static void Postfix(SonarScreenFX __instance)
        {
            SonarStereoTrace.Log($"SonarScreenFX.Ping: pingDistance={__instance.pingDistance} waveDuration={__instance.waveDuration} shaderID={__instance.pingDistanceShaderID} material={__instance._material?.name}");
        }
    }

    #endregion
}

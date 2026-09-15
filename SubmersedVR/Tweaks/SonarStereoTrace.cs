using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Diagnostic (temporary): find the Seamoth sonar ping visual. The v2 trace
    // showed the ping does not go through VFXScan/VFXScanning/SonarScreenFX, so
    // v3 dumps the candidate objects (name match) at ping time - name, active,
    // position, parent chain, shader - and samples their positions alongside the
    // VR camera position while the ping is active, to find what follows the head
    // (and is therefore rendered differently in each eye in stereo).
    // UnityEngine engine methods (Shader.SetGlobal*, Material.Set*) are native
    // with no managed body and cannot be patched with Harmony, so this trace
    // only patches game assembly methods.
    static class SonarStereoTrace
    {
        private const float WindowDuration = 3f;

        // Candidate sampling cadence (frames) and limits
        private const int SampleEveryFrames = 30;
        private const int MaxSampledCandidates = 10;
        private const int MaxDumpedCandidates = 40;

        static float windowEnd = -1f;
        static bool windowActive;
        static List<GameObject> candidates = new List<GameObject>();
        static int sampleFrame = -1;

        public static bool WindowOpen => windowActive && Time.time <= windowEnd;

        public static void OpenWindow(string source)
        {
            if (WindowOpen) return;

            windowEnd = Time.time + WindowDuration;
            windowActive = true;
            sampleFrame = -1;
            SonarTraceEyeLogger.ResetSeen();
            Mod.logger.LogInfo($"[SonarTrace] ping ({source}) - log window open for {WindowDuration}s");

            var root = SNCameraRoot.main;
            if (root != null)
            {
                Mod.logger.LogInfo($"[SonarTrace] stereoSeparation={root.stereoSeparation}");
                LogMatrix("matrixLeftEye", root.matrixLeftEye);
                LogMatrix("matrixRightEye", root.matrixRightEye);
            }
            DumpCandidates();
        }

        // One line per row: the logger swallows newlines in multi-line strings
        static void LogMatrix(string name, Matrix4x4 m)
        {
            Mod.logger.LogInfo($"[SonarTrace] {name} row0: {m.m00} {m.m01} {m.m02} {m.m03}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row1: {m.m10} {m.m11} {m.m12} {m.m13}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row2: {m.m20} {m.m21} {m.m22} {m.m23}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row3: {m.m30} {m.m31} {m.m32} {m.m33}");
        }

        // List the candidate objects at ping time: name, active state, world
        // position, parent chain (up to 5 levels) and shader if it has one
        static void DumpCandidates()
        {
            candidates.Clear();
            // Active objects only (the stub assembly lacks the includeInactive
            // overload): the ping visual must be active at ping time
            var all = Object.FindObjectsOfType<GameObject>();
            int dumped = 0;
            foreach (var go in all)
            {
                if (dumped >= MaxDumpedCandidates) break;
                if (!IsCandidateName(go.name)) continue;
                candidates.Add(go);
                string shader = "-";
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    shader = renderer.sharedMaterial.shader.name;
                }
                Mod.logger.LogInfo($"[SonarTrace] candidate: {ParentChain(go.transform)} active={go.activeInHierarchy} pos={go.transform.position} shader={shader}");
                dumped++;
            }
            Mod.logger.LogInfo($"[SonarTrace] candidate dump done ({dumped} objects)");
        }

        static string ParentChain(Transform t)
        {
            var parts = new List<string>(5);
            var cur = t;
            while (cur != null && parts.Count < 5)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            return string.Join(" > ", parts);
        }

        static bool IsCandidateName(string name)
        {
            return name.IndexOf("sonar", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ping", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("wave", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("scan", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ripple", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ring", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Log(string msg)
        {
            if (WindowOpen)
            {
                Mod.logger.LogInfo($"[SonarTrace] {msg}");
            }
        }

        // Sample the candidate positions alongside the VR camera position during
        // the window: if a candidate keeps a constant offset from the camera while
        // the head moves, it is camera-anchored (the stereo bug)
        public static void Sample()
        {
            if (!WindowOpen) return;
            if (sampleFrame >= 0 && Time.frameCount - sampleFrame < SampleEveryFrames) return;
            sampleFrame = Time.frameCount;

            var rig = VRCameraRig.instance;
            string camPos = rig != null && rig.vrCamera != null ? rig.vrCamera.transform.position.ToString() : "-";
            int sampled = 0;
            foreach (var go in candidates)
            {
                if (sampled >= MaxSampledCandidates) break;
                if (go == null || !go.activeInHierarchy) continue;
                Mod.logger.LogInfo($"[SonarTrace] sample: {go.name} pos={go.transform.position} camPos={camPos}");
                sampled++;
            }
        }
    }

    // Drives the candidate sampling while the trace window is open
    class SonarTraceSampler : MonoBehaviour
    {
        void Update()
        {
            SonarStereoTrace.Sample();
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
                SonarStereoTrace.Log($"render camera {Camera.current.name} {eye} pos={transform.position} stereoSep={Camera.current.stereoSeparation}");
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

    // Attach the eye logger to the actual VR camera (the one that renders the
    // stereo eyes) and the candidate sampler to the rig
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class SonarTraceAttachVR
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            if (__instance.vrCamera != null)
            {
                __instance.vrCamera.gameObject.GetOrAddComponent<SonarTraceEyeLogger>();
            }
            __instance.gameObject.GetOrAddComponent<SonarTraceSampler>();
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

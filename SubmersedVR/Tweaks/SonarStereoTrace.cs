// Temporary diagnostic: compiled out of release/PR builds unless SONAR_TRACE
// is defined in the csproj
#if SONAR_TRACE

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Diagnostic (temporary): find the Seamoth sonar ping visual. The v2 trace
    // showed the eye matrices are static (identity, half-IPD offset) and the
    // v4 global radius dump filled its cap with fish. v6 dumps the whole
    // vehicle hierarchy and the main camera hierarchy (plus the camera's
    // parent chain and the mounted vehicle) at ping time, re-scans nearby
    // renderers (6 m) every 30 frames to catch an object spawned after the
    // ping, and logs the VFXScan/VFXScanning/SonarScreenFX markers
    // unconditionally so a call outside the ping window is not missed.
    // UnityEngine engine methods (Shader.SetGlobal*, Material.Set*) are native
    // with no managed body and cannot be patched with Harmony, so this trace
    // only patches game assembly methods.
    static class SonarStereoTrace
    {
        private const float WindowDuration = 3f;

        // Re-scan cadence (frames), radius and limits
        private const int RescanEveryFrames = 30;
        private const float RescanRadius = 6f;
        private const int MaxNewPerRescan = 10;
        private const int MaxDumpedPerHierarchy = 400;
        private const int MaxSampledCandidates = 15;

        static float windowEnd = -1f;
        static bool windowActive;
        static Vector3 origin;
        static bool originValid;
        static List<GameObject> candidates = new List<GameObject>();
        static HashSet<string> seen = new HashSet<string>();
        static int rescanFrame = -1;
        static int sampleFrame = -1;

        public static bool WindowOpen => windowActive && Time.time <= windowEnd;

        public static void OpenWindow(string source)
        {
            if (WindowOpen) return;

            windowEnd = Time.time + WindowDuration;
            windowActive = true;
            candidates.Clear();
            seen.Clear();
            rescanFrame = -1;
            sampleFrame = -1;
            SonarTraceEyeLogger.ResetSeen();
            Mod.logger.LogInfo($"[SonarTrace] ping ({source}) - log window open for {WindowDuration}s");

            var root = SNCameraRoot.main;
            var vehicle = Player.main?.currentMountedVehicle;
            if (vehicle != null)
            {
                Mod.logger.LogInfo($"[SonarTrace] currentMountedVehicle={vehicle.name} pos={vehicle.transform.position}");
            }
            else
            {
                Mod.logger.LogInfo("[SonarTrace] currentMountedVehicle=<none>");
            }
            originValid = false;
            if (root != null)
            {
                Mod.logger.LogInfo($"[SonarTrace] stereoSeparation={root.stereoSeparation}");
                LogMatrix("matrixLeftEye", root.matrixLeftEye);
                LogMatrix("matrixRightEye", root.matrixRightEye);
                if (root.mainCam != null)
                {
                    origin = root.mainCam.transform.position;
                    originValid = true;
                    LogParentChain("mainCam", root.mainCam.transform);
                }
            }
            if (!originValid)
            {
                Mod.logger.LogError("[SonarTrace] no valid origin (mainCam was null) - the re-scan is disabled for this window");
            }
            Mod.logger.LogInfo($"[SonarTrace] ping origin={origin}");
            // The wave is either on the vehicle (sonar is a vehicle component)
            // or anchored to the camera
            DumpHierarchy("vehicle", root != null ? root.gameObject : null);
            DumpHierarchy("camera", root != null && root.mainCam != null ? root.mainCam.gameObject : null);
        }

        // Walk up the scene hierarchy: tells whether the main camera (and thus a
        // camera-anchored wave) hangs under the vehicle or at the scene root
        static void LogParentChain(string label, Transform t)
        {
            int depth = 0;
            while (t != null && depth < 10)
            {
                Mod.logger.LogInfo($"[SonarTrace] {label} chain[{depth}] {t.name}");
                t = t.parent;
                depth++;
            }
        }

        // One line per row: the logger swallows newlines in multi-line strings
        static void LogMatrix(string name, Matrix4x4 m)
        {
            Mod.logger.LogInfo($"[SonarTrace] {name} row0: {m.m00} {m.m01} {m.m02} {m.m03}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row1: {m.m10} {m.m11} {m.m12} {m.m13}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row2: {m.m20} {m.m21} {m.m22} {m.m23}");
            Mod.logger.LogInfo($"[SonarTrace] {name} row3: {m.m30} {m.m31} {m.m32} {m.m33}");
        }

        // Log every object of the given hierarchy: name, active state, world
        // position, depth and shader if it has a renderer
        static void DumpHierarchy(string label, GameObject go)
        {
            if (go == null)
            {
                Mod.logger.LogInfo($"[SonarTrace] {label} hierarchy: <null>");
                return;
            }
            int dumped = 0;
            DumpHierarchyRecursive(label, go.transform, 0, ref dumped);
            Mod.logger.LogInfo($"[SonarTrace] {label} hierarchy dump done ({dumped} objects)");
        }

        static void DumpHierarchyRecursive(string label, Transform t, int depth, ref int dumped)
        {
            if (dumped >= MaxDumpedPerHierarchy) return;
            string shader = "-";
            var renderer = t.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                shader = renderer.sharedMaterial.shader.name;
                candidates.Add(t.gameObject);
            }
            // Also register the name so the re-scan only logs objects that
            // appeared after the ping
            seen.Add(t.name);
            Mod.logger.LogInfo($"[SonarTrace] {label} depth={depth} {t.name} active={t.gameObject.activeInHierarchy} pos={t.position} shader={shader}");
            dumped++;
            for (int i = 0; i < t.childCount; i++)
            {
                DumpHierarchyRecursive(label, t.GetChild(i), depth + 1, ref dumped);
            }
        }

        public static void Log(string msg)
        {
            if (WindowOpen)
            {
                Mod.logger.LogInfo($"[SonarTrace] {msg}");
            }
        }

        // Re-scan nearby renderers while the window is open: the ping visual
        // may be spawned a few frames after SonarPing. Logs new objects (by
        // name) only.
        public static void Rescan()
        {
            if (!WindowOpen || !originValid) return;
            if (rescanFrame >= 0 && Time.frameCount - rescanFrame < RescanEveryFrames) return;
            rescanFrame = Time.frameCount;

            var rig = VRCameraRig.instance;
            Vector3 camPos = rig != null && rig.vrCamera != null ? rig.vrCamera.transform.position : Vector3.zero;
            int newLogged = 0;
            var renderers = Object.FindObjectsOfType<Renderer>();
            foreach (var renderer in renderers)
            {
                var t = renderer.transform;
                if (Vector3.Distance(t.position, origin) >= RescanRadius) continue;
                if (!seen.Add(t.name)) continue;
                // Wave-like names are logged even past the per-rescan cap, so a
                // busy frame cannot mask the ping visual
                if (!IsWaveCandidateName(t.name) && newLogged >= MaxNewPerRescan) continue;
                if (renderer.sharedMaterial != null)
                {
                    candidates.Add(t.gameObject);
                }
                string shader = renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "-";
                Mod.logger.LogInfo($"[SonarTrace] new: {t.name} active={t.gameObject.activeInHierarchy} pos={t.position} deltaFromCam={t.position - camPos} camPos={camPos} shader={shader}");
                newLogged++;
            }
            if (newLogged > 0)
            {
                Mod.logger.LogInfo($"[SonarTrace] rescan done ({newLogged} new objects)");
            }
        }

        // The ping visual is likely named after its effect; such names are
        // always worth logging
        static bool IsWaveCandidateName(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("scan") || name.Contains("sonar") || name.Contains("wave") || name.Contains("ping");
        }

        // Sample the dumped candidate positions during the window. The offset
        // relative to the VR camera is the discriminator: constant while the
        // head moves = camera-anchored (the stereo bug), growing =
        // world-anchored wave.
        public static void Sample()
        {
            if (!WindowOpen) return;
            if (sampleFrame >= 0 && Time.frameCount - sampleFrame < RescanEveryFrames) return;
            sampleFrame = Time.frameCount;

            var rig = VRCameraRig.instance;
            if (rig == null || rig.vrCamera == null) return;
            Vector3 camPos = rig.vrCamera.transform.position;
            int sampled = 0;
            foreach (var go in candidates)
            {
                if (sampled >= MaxSampledCandidates) break;
                if (go == null || !go.activeInHierarchy) continue;
                if (go.GetComponent<Renderer>() == null) continue;
                Mod.logger.LogInfo($"[SonarTrace] sample: {go.name} pos={go.transform.position} deltaFromCam={go.transform.position - camPos} camPos={camPos}");
                sampled++;
            }
        }
    }

    // Drives the re-scan and candidate sampling while the trace window is open
    class SonarTraceSampler : MonoBehaviour
    {
        void Update()
        {
            SonarStereoTrace.Rescan();
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

    // The VR camera is stolen after SetupControllers (and can be re-stolen on
    // level loads), so attach the eye logger there
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.StealCamera))]
    static class SonarTraceStealCamera
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance, Camera camera)
        {
            if (camera != null)
            {
                camera.gameObject.GetOrAddComponent<SonarTraceEyeLogger>();
            }
        }
    }

    // Attach the candidate sampler to the rig
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class SonarTraceAttachVR
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
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

    // World scan sweep VFX. Logged unconditionally (not only during an open
    // window) so a call outside the ping window is still visible in the log.
    [HarmonyPatch(typeof(VFXScan), nameof(VFXScan.StartScan))]
    static class SonarTraceVFXScan
    {
        [HarmonyPostfix]
        static void Postfix(VFXScan __instance)
        {
            Mod.logger.LogInfo($"[SonarTrace] VFXScan.StartScan: active={__instance.scanActive} duration={__instance.scanDuration} renderers={__instance.renderers?.Length} window={SonarStereoTrace.WindowOpen}");
        }
    }

    // Per-camera CommandBuffer scan VFX (one buffer per eye in stereo). Logged
    // unconditionally, like the other VFX markers.
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
            Mod.logger.LogInfo($"[SonarTrace] VFXScanning.StartScan: material={mat?.name} renderers={__instance.renderersToScan?.Count} cameras=[{cameras}] window={SonarStereoTrace.WindowOpen}");
        }
    }

    // Screen-space sonar ping FX. Logged unconditionally (not only during an
    // open window) so a call outside the ping window is still visible in the log.
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.Ping))]
    static class SonarTraceScreenFX
    {
        [HarmonyPostfix]
        static void Postfix(SonarScreenFX __instance)
        {
            Mod.logger.LogInfo($"[SonarTrace] SonarScreenFX.Ping: pingDistance={__instance.pingDistance} waveDuration={__instance.waveDuration} shaderID={__instance.pingDistanceShaderID} material={__instance._material?.name} window={SonarStereoTrace.WindowOpen}");
        }
    }

    #endregion
}

#endif

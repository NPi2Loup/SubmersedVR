// Temporary diagnostic: compiled out of release/PR builds unless SONAR_TRACE
// is defined in the csproj
#if SONAR_TRACE

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Diagnostic (temporary): find the sonar ping wave. The in-game symptom
    // is a grid texture on object outlines whose anchoring follows the camera
    // position, misaligned between the two stereo eyes. v8 hunts the shader
    // that draws the grid: the re-scan (40 m) logs new objects and, above
    // all, shader swaps on pre-existing objects (the wave paints existing
    // geometry, it is not a spawned object), collects the materials of
    // sonar-named shaders and dumps their property values (material and
    // global uniforms) every 30 frames, to identify which uniform drives
    // the grid and whether it tracks the camera. The v7 trace showed the
    // SonarScreenFX material is null and the VFXScan/VFXScanning paths are
    // never called (Seamoth or Cyclops). The v2 trace showed the eye
    // matrices are static (identity, half-IPD offset) and MainCamera renders
    // "Both".
    // UnityEngine engine methods (Shader.SetGlobal*, Material.Set*) are native
    // with no managed body and cannot be patched with Harmony, so this trace
    // only patches game assembly methods.
    static class SonarStereoTrace
    {
        private const float WindowDuration = 3f;

        // Re-scan cadence (frames), radius and limits
        private const int RescanEveryFrames = 30;
        private const float RescanRadius = 40f;
        private const int MaxNewPerRescan = 10;
        private const int MaxSampledCandidates = 15;
        private const int MaxSonarMats = 3;

        static float windowEnd = -1f;
        static bool windowActive;
        static Vector3 origin;
        static bool originValid;
        public static Material fxMaterial;
        static List<GameObject> candidates = new List<GameObject>();
        static List<Material> sonarMats = new List<Material>();
        static Dictionary<Renderer, string> shaderSnapshot = new Dictionary<Renderer, string>();
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
            sonarMats.Clear();
            seen.Clear();
            rescanFrame = -1;
            sampleFrame = -1;
            fxMaterial = null;
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
            // Register the main camera hierarchy names so the re-scan does not
            // log the camera objects as "new" (the v6 "vehicle" dump was the
            // PlayerCameras object, not the vehicle)
            if (root != null && root.mainCam != null)
            {
                AddNamesToSeen(root.mainCam.transform);
            }
            // Snapshot the shader of every renderer so the re-scans can log
            // the swap the ping performs on pre-existing objects: the wave is
            // painted on existing geometry, it is not a spawned object
            shaderSnapshot.Clear();
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                shaderSnapshot[renderer] = renderer.sharedMaterial?.shader?.name ?? "-";
            }
            Mod.logger.LogInfo($"[SonarTrace] shader snapshot: {shaderSnapshot.Count} renderers");
        }

        static void AddNamesToSeen(Transform t)
        {
            seen.Add(t.name);
            for (int i = 0; i < t.childCount; i++)
            {
                AddNamesToSeen(t.GetChild(i));
            }
        }

        // Walk up the scene hierarchy: tells whether an object hangs under the
        // vehicle, under the (stolen) camera or at the scene root
        public static void LogParentChain(string label, Transform t)
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
                string shader = renderer.sharedMaterial?.shader?.name ?? "-";

                // The ping may paint pre-existing geometry: a shader change on
                // an object that was already there is the wave signature
                string oldShader;
                if (shaderSnapshot.TryGetValue(renderer, out oldShader) && oldShader != shader)
                {
                    shaderSnapshot[renderer] = shader;
                    string swapParent = t.parent != null ? t.parent.name : "<none>";
                    Mod.logger.LogInfo($"[SonarTrace] shaderSwap: {t.name} {oldShader} -> {shader} pos={t.position} camPos={camPos} parent={swapParent} comps={LogComponentTypes(t)}");
                    LogMaterialProperties(renderer.sharedMaterial);
                }

                if (IsSonarShader(shader))
                {
                    var mat = renderer.sharedMaterial;
                    if (mat != null && !sonarMats.Contains(mat))
                    {
                        sonarMats.Add(mat);
                    }
                }

                if (!seen.Add(t.name)) continue;
                // Wave-like names are logged even past the per-rescan cap, so a
                // busy frame cannot mask the ping visual
                if (!IsWaveCandidateName(t.name) && newLogged >= MaxNewPerRescan) continue;
                if (renderer.sharedMaterial != null)
                {
                    candidates.Add(t.gameObject);
                }
                string parent = t.parent != null ? t.parent.name : "<none>";
                string grand = t.parent != null && t.parent.parent != null ? t.parent.parent.name : "<none>";
                Mod.logger.LogInfo($"[SonarTrace] new: {t.name} active={t.gameObject.activeInHierarchy} pos={t.position} deltaFromCam={t.position - camPos} camPos={camPos} shader={shader} parent={parent} gp={grand}");
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

        // The shader that draws the sonar grid (FX/WBOIT-CyclopsSonar and
        // friends); matched by name so both vehicle variants are caught
        static bool IsSonarShader(string shaderName)
        {
            if (shaderName == null) return false;
            shaderName = shaderName.ToLowerInvariant();
            return shaderName.Contains("sonar") || shaderName.Contains("wboit");
        }

        // The script component names on the object: identifies the game class
        // that owns the sonar visual
        static string LogComponentTypes(Transform t)
        {
            var comps = t.GetComponents<Component>();
            var names = new List<string>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var tn = c.GetType().Name;
                if (!names.Contains(tn)) names.Add(tn);
            }
            return string.Join(",", names);
        }

        // Log the current values of the material's numeric properties, both
        // the per-material value and the global uniform: the grid may be
        // driven by a global set once per ping (or per eye). Which one
        // animates over the wave, and how it correlates with the camera, is
        // the fix target
        public static void LogMaterialProperties(Material m)
        {
            if (m == null) return;
            var shader = m.shader;
            if (shader == null) return;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                if (type == UnityEngine.Rendering.ShaderPropertyType.Float)
                {
                    Mod.logger.LogInfo($"[SonarTrace] fxmat {m.name}.{name} mat={m.GetFloat(name):0.###} global={GlobalFloat(name):0.###}");
                }
                else if (type == UnityEngine.Rendering.ShaderPropertyType.Vector)
                {
                    Mod.logger.LogInfo($"[SonarTrace] fxmat {m.name}.{name} mat={m.GetVector(name)} global={GlobalVector(name)}");
                }
                else if (type == UnityEngine.Rendering.ShaderPropertyType.Color)
                {
                    Mod.logger.LogInfo($"[SonarTrace] fxmat {m.name}.{name} mat={m.GetColor(name)} global={GlobalColor(name)}");
                }
                else continue;
            }
        }

        static float GlobalFloat(string name)
        {
            try { return Shader.GetGlobalFloat(name); }
            catch { return -12345f; }
        }

        static Vector4 GlobalVector(string name)
        {
            try { return Shader.GetGlobalVector(name); }
            catch { return new Vector4(-12345f, -12345f, -12345f, -12345f); }
        }

        static Color GlobalColor(string name)
        {
            try { return Shader.GetGlobalColor(name); }
            catch { return Color.magenta; }
        }

        // Sample the candidate positions and the screen FX material values
        // during the window. The offset relative to the VR camera is the
        // discriminator: constant while the head moves = camera-anchored
        // (the stereo bug), growing = world-anchored wave.
        public static void Sample()
        {
            if (!WindowOpen) return;
            if (sampleFrame >= 0 && Time.frameCount - sampleFrame < RescanEveryFrames) return;
            sampleFrame = Time.frameCount;

            if (fxMaterial != null)
            {
                LogMaterialProperties(fxMaterial);
            }

            var rig = VRCameraRig.instance;
            if (rig == null || rig.vrCamera == null) return;
            Vector3 camPos = rig.vrCamera.transform.position;

            // The sonar-shader materials: their property values over the
            // window, against camPos and the ping origin, reveal the grid
            // driver (constant vs camera-tracking)
            int matDumped = 0;
            foreach (var m in sonarMats)
            {
                if (matDumped >= MaxSonarMats) break;
                if (m == null || m.shader == null) continue;
                matDumped++;
                Mod.logger.LogInfo($"[SonarTrace] sonarmat {m.name} shader={m.shader.name} cam={camPos} origin={origin}");
                LogMaterialProperties(m);
            }

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
    // open window) so a call outside the ping window is still visible in the
    // log. Also logs the component context and the material property values:
    // the grid texture is likely driven by one of these properties.
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.Ping))]
    static class SonarTraceScreenFX
    {
        [HarmonyPostfix]
        static void Postfix(SonarScreenFX __instance)
        {
            Mod.logger.LogInfo($"[SonarTrace] SonarScreenFX.Ping: pingDistance={__instance.pingDistance} waveDuration={__instance.waveDuration} shaderID={__instance.pingDistanceShaderID} material={__instance._material?.name} window={SonarStereoTrace.WindowOpen}");
            var go = __instance.gameObject;
            Mod.logger.LogInfo($"[SonarTrace] SonarScreenFX go={go.name} active={go.activeInHierarchy} pos={go.transform.position}");
            SonarStereoTrace.LogParentChain("SonarScreenFX", go.transform);
            SonarStereoTrace.fxMaterial = __instance._material;
            SonarStereoTrace.LogMaterialProperties(__instance._material);
        }
    }

    #endregion
}

#endif

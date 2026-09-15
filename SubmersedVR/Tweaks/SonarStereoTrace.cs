using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // Diagnostic (temporary): log global shader uniform writes and per-eye camera
    // positions while a sonar ping is active, to find what makes the scan wave
    // render differently in each eye in stereo VR.
    static class SonarStereoTrace
    {
        private const float WindowDuration = 3f;
        private const int MaxLines = 500;

        public static float windowEnd = -1f;
        static int lines;
        static HashSet<string> seen;
        static HashSet<string> seenEye;
        public static Dictionary<int, string> idToName = new Dictionary<int, string>();

        public static bool WindowOpen => Time.time <= windowEnd;

        public static void OpenWindow(string source)
        {
            if (windowEnd < Time.time)
            {
                windowEnd = Time.time + WindowDuration;
                lines = 0;
                seen = new HashSet<string>();
                seenEye = new HashSet<string>();
                Mod.logger.LogInfo($"[SonarTrace] ping ({source}) - log window open for {WindowDuration}s");
                if (SNCameraRoot.main != null)
                {
                    Mod.logger.LogInfo($"[SonarTrace] stereoSeparation = {SNCameraRoot.main.stereoSeparation}");
                }
            }
        }

        public static void LogCall(string name, string value)
        {
            if (!WindowOpen || lines >= MaxLines || name == null) return;
            string key = name + "=" + value;
            if (seen.Add(key))
            {
                lines++;
                Mod.logger.LogInfo($"[SonarTrace] {key}");
            }
        }

        // Material properties are written every frame for many materials,
        // so only log the ones that look sonar-related
        public static bool IsSonarName(string name)
        {
            if (name == null) return false;
            return name.IndexOf("scan", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sonar", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ping", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("wave", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void LogEye(string eye, string pos)
        {
            if (!WindowOpen) return;
            if (seenEye.Add(eye))
            {
                Mod.logger.LogInfo($"[SonarTrace] render camera {eye} pos {pos}");
            }
        }
    }

    // Logs the render camera position once per stereo eye during the trace window
    class SonarTraceEyeLogger : MonoBehaviour
    {
        void OnPreRender()
        {
            if (!SonarStereoTrace.WindowOpen || Camera.current == null) return;
            SonarStereoTrace.LogEye(Camera.current.stereoTargetEye.ToString(), transform.position.ToString());
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

    // Record PropertyToID mappings so ID-based uniform writes can be named
    [HarmonyPatch(typeof(Shader), nameof(Shader.PropertyToID))]
    static class SonarTracePropertyToID
    {
        [HarmonyPostfix]
        static void Postfix(string name, int __result)
        {
            SonarStereoTrace.idToName[__result] = name;
        }
    }

    // Global shader uniform writes during the window
    [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalFloat))]
    static class SonarTraceSetGlobalFloat
    {
        [HarmonyPrefix]
        static void Prefix(string name, float value)
        {
            SonarStereoTrace.LogCall(name, value.ToString("F4"));
        }

        [HarmonyPrefix]
        static void Prefix(int nameID, float value)
        {
            SonarStereoTrace.LogCall(SonarStereoTrace.idToName.TryGetValue(nameID, out var name) ? name : "id" + nameID, value.ToString("F4"));
        }
    }

    [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalVector))]
    static class SonarTraceSetGlobalVector
    {
        [HarmonyPrefix]
        static void Prefix(string name, Vector3 value)
        {
            SonarStereoTrace.LogCall(name, value.ToString("F3"));
        }

        [HarmonyPrefix]
        static void Prefix(int nameID, Vector3 value)
        {
            SonarStereoTrace.LogCall(SonarStereoTrace.idToName.TryGetValue(nameID, out var name) ? name : "id" + nameID, value.ToString("F3"));
        }
    }

    [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalColor))]
    static class SonarTraceSetGlobalColor
    {
        [HarmonyPrefix]
        static void Prefix(string name, Color value)
        {
            SonarStereoTrace.LogCall(name, value.ToString("F3"));
        }

        [HarmonyPrefix]
        static void Prefix(int nameID, Color value)
        {
            SonarStereoTrace.LogCall(SonarStereoTrace.idToName.TryGetValue(nameID, out var name) ? name : "id" + nameID, value.ToString("F3"));
        }
    }

    [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalInt))]
    static class SonarTraceSetGlobalInt
    {
        [HarmonyPrefix]
        static void Prefix(string name, int value)
        {
            SonarStereoTrace.LogCall(name, value.ToString());
        }

        [HarmonyPrefix]
        static void Prefix(int nameID, int value)
        {
            SonarStereoTrace.LogCall(SonarStereoTrace.idToName.TryGetValue(nameID, out var name) ? name : "id" + nameID, value.ToString());
        }
    }

    // Material uniform writes, only the sonar-looking names
    [HarmonyPatch(typeof(Material), nameof(Material.SetFloat))]
    static class SonarTraceMaterialSetFloat
    {
        [HarmonyPrefix]
        static void Prefix(Material material, string name, float value)
        {
            if (SonarStereoTrace.IsSonarName(name))
            {
                SonarStereoTrace.LogCall("mat." + material.name + "." + name, value.ToString("F4"));
            }
        }
    }

    [HarmonyPatch(typeof(Material), nameof(Material.SetVector))]
    static class SonarTraceMaterialSetVector
    {
        [HarmonyPrefix]
        static void Prefix(Material material, string name, Vector3 value)
        {
            if (SonarStereoTrace.IsSonarName(name))
            {
                SonarStereoTrace.LogCall("mat." + material.name + "." + name, value.ToString("F3"));
            }
        }
    }

    [HarmonyPatch(typeof(Material), nameof(Material.SetColor))]
    static class SonarTraceMaterialSetColor
    {
        [HarmonyPrefix]
        static void Prefix(Material material, string name, Color value)
        {
            if (SonarStereoTrace.IsSonarName(name))
            {
                SonarStereoTrace.LogCall("mat." + material.name + "." + name, value.ToString("F3"));
            }
        }
    }

    #endregion
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // World-anchored sonar ping visual (the fix). The in-game grid is a
    // regular renderer (SonarBase, FX/WBOIT-CyclopsSonar) whose material
    // properties are static: the grid phase is driven in the shader by a
    // per-eye GPU builtin (the camera position in single-pass stereo), which
    // cannot be overridden from managed code. Instead, at ping time the sonar
    // renderers' materials are swapped for a world-space additive grid frozen
    // at the ping origin (the camera position at trigger time), plus an
    // expanding wave ring. Both eyes see the same, stable visual.
    static class SonarWorldPing
    {
        private const float MaxRadius = 10f;
        private const float SearchRadius = 50f;
        private const float GridScrollSpeed = 0.3f;
        private const float GridFadeIn = 0.3f;
        private const float GridFadeOut = 1f;

        private static readonly Color FallbackPingColor = new Color(0.253f, 0.593f, 0.662f, 0.196f);
        private static readonly string[] AdditiveShaderNames =
        {
            "Particles/Additive",
            "Sprites/Default",
            "UWE/Standard",
            "Universal Render Pipeline/Particles/Unlit",
            "Hidden/Universal Default"
        };

        static Shader additiveShader;
        static Texture2D gridTexture;
        static Texture2D ringTexture;

        static bool active;
        static float pingStartTime;
        static float duration;
        internal static float waveDuration = 5f;
        static Vector3 origin;
        static GameObject ringGo;
        static Material ringMat;
        static float ringBaseAlpha;

        static List<Renderer> swappedRenderers = new List<Renderer>();
        static List<Material> originalMaterials = new List<Material>();
        static List<Material> gridMaterials = new List<Material>();
        static List<Color> gridBaseColors = new List<Color>();

        public static void Trigger()
        {
            var root = SNCameraRoot.main;
            if (root == null || root.mainCam == null) return;
            var camTransform = root.mainCam.transform;
            if (camTransform == null) return;
            if (waveDuration <= 0f) return;

            // Both ping entry points fire on the same ping: ignore the second
            // (the check must precede Restore, which clears 'active')
            if (active && Time.time - pingStartTime < 0.5f) return;

            if (active) Restore();

            var shader = AdditiveShader();
            if (shader == null) return;

            var camPos = camTransform.position;
            var camForward = camTransform.forward;

            swappedRenderers.Clear();
            originalMaterials.Clear();
            gridMaterials.Clear();
            gridBaseColors.Clear();

            // The renderers the ping paints: sonar-named shared materials
            // near the ping origin
            Color pingColor = FallbackPingColor;
            var renderers = Object.FindObjectsOfType<Renderer>();
            foreach (var renderer in renderers)
            {
                // UI renderers (HUD sonar) share sonar-named shaders: skip them
                if (renderer is CanvasRenderer) continue;
                var mat = renderer.sharedMaterial;
                if (mat == null || mat.shader == null) continue;
                if (mat.shader.name == null || mat.shader.name.ToLowerInvariant().IndexOf("sonar") < 0) continue;
                if (Vector3.Distance(renderer.transform.position, camPos) >= SearchRadius) continue;

                Color color = mat.HasProperty("_PingColor") ? mat.GetColor("_PingColor") : FallbackPingColor;
                var newMat = new Material(shader);
                newMat.mainTexture = GridTexture();
                newMat.color = color;

                swappedRenderers.Add(renderer);
                originalMaterials.Add(mat);
                gridMaterials.Add(newMat);
                gridBaseColors.Add(color);
                if (gridMaterials.Count == 1) pingColor = color;
                renderer.sharedMaterial = newMat;
            }

            CreateRing(camPos, camForward, shader, pingColor);

            active = true;
            pingStartTime = Time.time;
            duration = waveDuration;
            origin = camPos;
            Mod.logger.LogInfo($"[SonarWorld] ping: swapped {swappedRenderers.Count} renderers at origin={origin}");
        }

        static void CreateRing(Vector3 position, Vector3 camForward, Shader shader, Color color)
        {
            ringGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ringGo.name = "SonarPingRing";
            Object.Destroy(ringGo.GetComponent<Collider>());
            // Offset along the frozen forward so the ring clears the camera
            // near plane (a quad exactly at the camera is clipped)
            ringGo.transform.position = position + camForward * 1f;
            // Orientation frozen at ping time: the ring faces the camera rig
            // as it was when the ping fired, so it stays put in world space
            ringGo.transform.rotation = Quaternion.LookRotation(camForward, Vector3.up);
            ringMat = new Material(shader);
            ringMat.mainTexture = RingTexture();
            ringMat.color = color;
            ringMat.renderQueue = 3100;
            ringBaseAlpha = color.a;
            var ringRenderer = ringGo.GetComponent<Renderer>();
            if (ringRenderer != null)
            {
                ringRenderer.sharedMaterial = ringMat;
            }
            ringGo.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        }

        public static void Update()
        {
            if (!active) return;
            float t = Time.time - pingStartTime;

            if (ringGo != null && ringMat != null)
            {
                float s = MaxRadius * 2f * (t / duration);
                ringGo.transform.localScale = new Vector3(s, s, s);
                var c = ringMat.color;
                c.a = ringBaseAlpha * Mathf.Clamp01(1f - t / duration);
                ringMat.color = c;
            }

            for (int i = 0; i < gridMaterials.Count; i++)
            {
                var m = gridMaterials[i];
                if (m == null) continue;
                m.mainTextureOffset += Vector2.right * GridScrollSpeed * Time.deltaTime;
                float fade = 1f;
                if (t < GridFadeIn)
                {
                    fade = Mathf.Clamp01(t / GridFadeIn);
                }
                else if (t > duration)
                {
                    fade = Mathf.Clamp01(1f - (t - duration) / GridFadeOut);
                }
                var c = gridBaseColors[i];
                c.a = c.a * fade;
                m.color = c;
            }

            if (t > duration + GridFadeOut)
            {
                Restore();
            }
        }

        static void Restore()
        {
            for (int i = 0; i < swappedRenderers.Count; i++)
            {
                var renderer = swappedRenderers[i];
                var original = originalMaterials[i];
                if (renderer != null && original != null)
                {
                    renderer.sharedMaterial = original;
                }
            }
            for (int i = 0; i < gridMaterials.Count; i++)
            {
                if (gridMaterials[i] != null)
                {
                    Object.Destroy(gridMaterials[i]);
                }
            }
            if (ringMat != null)
            {
                Object.Destroy(ringMat);
            }
            swappedRenderers.Clear();
            originalMaterials.Clear();
            gridMaterials.Clear();
            gridBaseColors.Clear();
            if (ringGo != null)
            {
                Object.Destroy(ringGo);
            }
            ringGo = null;
            ringMat = null;
            active = false;
        }

        // The additive shader is found once and cached: the first available
        // name wins. The error log marks a build where no usable shader was
        // found, in which case the ping visual is skipped (graceful)
        static Shader AdditiveShader()
        {
            if (additiveShader == null)
            {
                foreach (var name in AdditiveShaderNames)
                {
                    additiveShader = Shader.Find(name);
                    if (additiveShader != null) break;
                }
                if (additiveShader != null)
                {
                    Mod.logger.LogInfo($"[SonarWorld] additive shader={additiveShader.name}");
                }
                else
                {
                    Mod.logger.LogError("[SonarWorld] no usable additive shader found (tried Particles/Additive, Sprites/Default, UWE/Standard, URP/unlit fallbacks) - ping visual disabled");
                }
            }
            return additiveShader;
        }

        // 256x256 white grid on transparent, repeated: 2px lines every 16px,
        // point-filtered. RGBA32 (not Alpha8): an alpha-only texture would
        // sample as R in the additive shader and tint the grid red
        static Texture2D GridTexture()
        {
            if (gridTexture != null) return gridTexture;
            gridTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            gridTexture.name = "SonarGrid";
            gridTexture.filterMode = FilterMode.Point;
            gridTexture.wrapMode = TextureWrapMode.Repeat;
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    bool line = x % 16 < 2 || y % 16 < 2;
                    gridTexture.SetPixel(x, y, line ? Color.white : Color.clear);
                }
            }
            gridTexture.Apply();
            return gridTexture;
        }

        // 256x256 white ring band at the texture edge (radius ~124/128): the
        // quad is scaled to 2x MaxRadius, so the visible ring reaches
        // MaxRadius at the end of the wave
        static Texture2D RingTexture()
        {
            if (ringTexture != null) return ringTexture;
            ringTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            ringTexture.name = "SonarRing";
            float center = 127.5f;
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float dx = x + 0.5f - center;
                    float dy = y + 0.5f - center;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = 1f;
                    if (d < 121f)
                    {
                        a = 1f - (121f - d) / 2f;
                    }
                    else if (d > 127f)
                    {
                        a = 1f - (d - 127f) / 2f;
                    }
                    a = Mathf.Clamp01(a);
                    a = a * a * (3f - 2f * a);
                    ringTexture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            ringTexture.Apply();
            return ringTexture;
        }
    }

    // Drives the world-anchored ping visual at the Update cadence
    class SonarWorldPingDriver : MonoBehaviour
    {
        void Update()
        {
            SonarWorldPing.Update();
        }
    }

    #region Patches

    // Attach the ping driver to the camera rig (other postfixes on the same
    // method already exist: the trace sampler, the PDA, ...)
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class AttachSonarWorldPing
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            __instance.gameObject.GetOrAddComponent<SonarWorldPingDriver>();
        }
    }

    // Sonar ping entry points (shared Seamoth/Cyclops + Cyclops button); both
    // fire on the same ping, Trigger has a cooldown guard
    [HarmonyPatch(typeof(SNCameraRoot), nameof(SNCameraRoot.SonarPing))]
    static class SonarWorldPingSNCameraRoot
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            SonarWorldPing.Trigger();
        }
    }

    [HarmonyPatch(typeof(CyclopsSonarButton), nameof(CyclopsSonarButton.SonarPing))]
    static class SonarWorldPingCyclopsButton
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            SonarWorldPing.Trigger();
        }
    }

    // Capture the wave duration from the screen FX ping
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.Ping))]
    static class SonarWorldPingWaveDuration
    {
        [HarmonyPostfix]
        static void Postfix(SonarScreenFX __instance)
        {
            if (__instance.waveDuration > 0f)
            {
                SonarWorldPing.waveDuration = __instance.waveDuration;
            }
        }
    }

    #endregion
}

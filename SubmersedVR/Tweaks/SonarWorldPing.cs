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
            // Confirmed present in-game (used by the ObjectivePing FX)
            "Legacy Shaders/Particles/Additive",
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
        // Full material slot arrays, restored on ping end
        static List<Material[]> originalMaterials = new List<Material[]>();
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

            // The ping repaints the whole hologram subtree (e.g.
            // SonarMap_Small): every eye-dependent material in it (the grid,
            // the mini sub, the projector) would otherwise keep the per-eye
            // phase. The first sonar-named material provides the ping color.
            Color pingColor = FallbackPingColor;
            bool foundPingColor = false;
            var renderers = Object.FindObjectsOfType<Renderer>();
            foreach (var renderer in renderers)
            {
                // UI renderers (HUD sonar) share sonar-named shaders: skip them
                if (renderer is CanvasRenderer) continue;
                if (Vector3.Distance(renderer.transform.position, camPos) >= SearchRadius) continue;
                if (!IsHologramRenderer(renderer)) continue;

                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                var newMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    Color color = FallbackPingColor;
                    if (mats[i] != null && mats[i].HasProperty("_PingColor"))
                    {
                        color = mats[i].GetColor("_PingColor");
                        foundPingColor = true;
                    }
                    var newMat = new Material(shader);
                    newMat.mainTexture = GridTexture();
                    newMat.color = color;
                    newMat.renderQueue = 3100;
                    newMats[i] = newMat;
                    gridMaterials.Add(newMat);
                    gridBaseColors.Add(color);
                }
                pingColor = foundPingColor ? newMats[0].color : FallbackPingColor;

                swappedRenderers.Add(renderer);
                originalMaterials.Add(mats);
                renderer.sharedMaterials = newMats;

                var parentName = renderer.transform.parent != null ? renderer.transform.parent.name : "?";
                Mod.logger.LogInfo($"[SonarWorld] swap: {renderer.name} (parent={parentName}) {mats.Length} slot(s): {DescribeMats(mats)}");
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
                    renderer.sharedMaterials = original;
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

        // A renderer belongs to the sonar hologram if one of its few parent
        // transforms is the hologram root (SonarMap_*), or as a fallback if a
        // material uses a sonar-named shader (e.g. the grid pass)
        static bool IsHologramRenderer(Renderer renderer)
        {
            var t = renderer.transform;
            for (int i = 0; i < 3 && t != null; i++)
            {
                var n = t.name.ToLowerInvariant();
                if (n.IndexOf("sonarmap") >= 0 || n.IndexOf("hologram") >= 0)
                {
                    return true;
                }
                t = t.parent;
            }
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null && mat.shader != null && mat.shader.name != null
                    && mat.shader.name.ToLowerInvariant().IndexOf("sonar") >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        static string DescribeMats(Material[] mats)
        {
            var parts = new List<string>(mats.Length);
            foreach (var mat in mats)
            {
                parts.Add(mat != null && mat.shader != null ? mat.shader.name : "null");
            }
            return string.Join(", ", parts);
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

        // 512x512 grid on transparent, repeated: 1px minor lines every 32px,
        // 2px major lines every 128px, bilinear-filtered (Point made the
        // stretched texture read as blocky low-res in-game). RGBA32 (not
        // Alpha8): an alpha-only texture would sample as R in the additive
        // shader and tint the grid red
        static Texture2D GridTexture()
        {
            if (gridTexture != null) return gridTexture;
            gridTexture = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            gridTexture.name = "SonarGrid";
            gridTexture.filterMode = FilterMode.Bilinear;
            gridTexture.wrapMode = TextureWrapMode.Repeat;
            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    bool major = x % 128 < 2 || y % 128 < 2;
                    bool minor = x % 32 == 0 || y % 32 == 0;
                    Color c;
                    if (major)
                    {
                        c = new Color(1f, 1f, 1f, 0.9f);
                    }
                    else if (minor)
                    {
                        c = new Color(1f, 1f, 1f, 0.45f);
                    }
                    else
                    {
                        c = Color.clear;
                    }
                    gridTexture.SetPixel(x, y, c);
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

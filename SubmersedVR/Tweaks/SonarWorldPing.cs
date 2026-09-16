using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // World-anchored sonar grid (the fix). The game draws its grid on the
    // hologram's own meshes (notably the screen-projected "projector", which
    // is why the grid follows the head and desyncs between the two eyes in
    // single-pass stereo - the WBOIT composite pass itself carries no sonar
    // overlay, confirmed in-game: the FillBuffer prefix below never fires).
    // The fix: repaint ALL hologram meshes with world-space additive
    // materials - the dedicated grid mesh (sonar-named shader) gets the
    // grid texture, the model meshes (mini-sub, projector, light cone) get
    // a plain glow - kept for as long as the hologram is visible (like the
    // non-VR minimap), plus an expanding wave ring on each ping. Both eyes
    // then see the same, stable visual.
    static class SonarWorldPing
    {
        private const float MaxRadius = 10f;
        private const float SearchRadius = 50f;
        private const float GridScrollSpeed = 0.3f;
        private const float GridFadeIn = 0.3f;
        // Alpha multiplier for the plain (non-grid) hologram materials
        private const float PlainAlphaScale = 0.25f;
        // Hysteresis: the hologram must stay hidden this many frames before
        // the grid material is restored (avoids swap/restore churn)
        private const int HiddenFramesToRestore = 60;
        // Scene-wide rescan cadence while the grid is not yet applied
        private const int RetryRescanFrames = 60;

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

        static string additiveShaderName;
        // Adopted from the game's ObjectivePing particle (the runtime
        // Shader.Find database does not include its additive shader)
        static Shader adoptedShader;
        static int lastAdoptFrame;
        static bool updateExceptionLogged;
        static Texture2D gridTexture;
        static Texture2D ringTexture;

        // Ping wave (ring) state
        static bool hasPinged;
        static float ringStartTime;
        static float duration;
        internal static float waveDuration = 5f;
        static Vector3 origin;
        static GameObject ringGo;
        static Material ringMat;
        static float ringBaseAlpha;

        // Stable grid state (persistent while the hologram is visible)
        static bool swapped;
        static float swapTime;
        static int hiddenFrames;
        static int lastRetryFrame;
        static List<Renderer> swappedRenderers = new List<Renderer>();
        static List<Material[]> originalMaterials = new List<Material[]>();
        static List<Material> gridMaterials = new List<Material>();
        static List<Color> gridBaseColors = new List<Color>();
        // Per-material flag: true = grid texture (UV scroll), false = plain
        static List<bool> gridSlots = new List<bool>();

        public static void Trigger()
        {
            if (Mod.quitting) return;
            var root = SNCameraRoot.main;
            if (root == null || root.mainCam == null) return;
            var camTransform = root.mainCam.transform;
            if (camTransform == null) return;
            if (waveDuration <= 0f) return;

            // Both ping entry points fire on the same ping: ignore the second
            if (hasPinged && Time.time - ringStartTime < 0.5f) return;

            hasPinged = true;
            ringStartTime = Time.time;
            duration = waveDuration;
            origin = camTransform.position;

            // The ping spawns the game's ObjectivePing particle: grab its
            // additive shader (not in the runtime Shader.Find database)
            try
            {
                TryAdoptAdditiveShader();
                EnsureSwapped();
                CreateRing(camTransform.position, camTransform.forward);
                Mod.logger.LogInfo($"[SonarWorld] ping: swapped {swappedRenderers.Count} renderers at origin={origin}");
            }
            catch (System.Exception e)
            {
                if (!updateExceptionLogged)
                {
                    updateExceptionLogged = true;
                    Mod.logger.LogError($"[SonarWorld] Trigger exception: {e}");
                }
            }
        }

        // Swap ALL hologram renderers for world-space additive materials:
        // the dedicated grid mesh (sonar-named material) gets the grid
        // texture, the model meshes (mini-sub, projector, light cone) get a
        // plain glow - the projector is what draws the camera-following
        // grid. Called on each ping and periodically until the hologram is
        // found (it may spawn between pings, e.g. on sonar activation)
        static void EnsureSwapped()
        {
            if (swapped) return;
            var root = SNCameraRoot.main;
            if (root == null || root.mainCam == null) return;
            var camTransform = root.mainCam.transform;
            if (camTransform == null) return;
            var camPos = camTransform.position;

            var shader = AdditiveShader();
            if (shader == null) return;

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
                bool anyGrid = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var orig = mats[i];
                    bool grid = orig != null && orig.shader != null && orig.shader.name != null
                        && orig.shader.name.ToLowerInvariant().IndexOf("sonar") >= 0;
                    Color color = orig != null && orig.HasProperty("_PingColor")
                        ? orig.GetColor("_PingColor") : FallbackPingColor;
                    var newMat = new Material(shader);
                    if (grid)
                    {
                        newMat.mainTexture = GridTexture();
                        anyGrid = true;
                    }
                    else
                    {
                        // Plain glow (no texture): keeps the silhouette and
                        // removes the mesh-drawn grid
                        color.a *= PlainAlphaScale;
                    }
                    newMat.color = color;
                    newMat.renderQueue = 3100;
                    newMats[i] = newMat;
                    gridMaterials.Add(newMat);
                    gridBaseColors.Add(color);
                    gridSlots.Add(grid);
                }
                swappedRenderers.Add(renderer);
                originalMaterials.Add(mats);
                renderer.sharedMaterials = newMats;
                swapped = true;
                swapTime = Time.time;
                hiddenFrames = 0;

                var parentName = renderer.transform.parent != null ? renderer.transform.parent.name : "?";
                Mod.logger.LogInfo($"[SonarWorld] swap: {renderer.name} (parent={parentName}) {mats.Length} slot(s): {DescribeMats(mats)} grid={anyGrid}");
            }
        }

        public static void Update()
        {
            if (Mod.quitting) return;
            try
            {
                // Adopt the game's additive shader once the ping's particle
                // exists (a few frames after the ping)
                TryAdoptAdditiveShader();

                // The stable grid persists while the hologram is visible
                // (like the non-VR minimap) and is restored once the
                // hologram is gone
                if (swapped)
                {
                    bool visible = false;
                    for (int i = 0; i < swappedRenderers.Count; i++)
                    {
                        var renderer = swappedRenderers[i];
                        if (renderer != null && renderer.gameObject.activeInHierarchy)
                        {
                            visible = true;
                            break;
                        }
                    }
                    if (visible)
                    {
                        hiddenFrames = 0;
                        float fade = Mathf.Clamp01((Time.time - swapTime) / GridFadeIn);
                        for (int i = 0; i < gridMaterials.Count; i++)
                        {
                            var mat = gridMaterials[i];
                            if (mat == null) continue;
                            if (gridSlots[i])
                            {
                                mat.mainTextureOffset += Vector2.right * GridScrollSpeed * Time.deltaTime;
                            }
                            var c = gridBaseColors[i];
                            c.a *= fade;
                            mat.color = c;
                        }
                    }
                    else if (++hiddenFrames >= HiddenFramesToRestore)
                    {
                        Restore();
                    }
                }
                else if (Time.frameCount - lastRetryFrame >= RetryRescanFrames)
                {
                    lastRetryFrame = Time.frameCount;
                    EnsureSwapped();
                }

                // Expanding wave ring on each ping
                if (ringGo != null && ringMat != null)
                {
                    float t = Time.time - ringStartTime;
                    float s = MaxRadius * 2f * (t / duration);
                    ringGo.transform.localScale = new Vector3(s, s, s);
                    var c = ringMat.color;
                    c.a = ringBaseAlpha * Mathf.Clamp01(1f - t / duration);
                    ringMat.color = c;
                    if (t > duration)
                    {
                        DestroyRing();
                    }
                }
            }
            catch (System.Exception e)
            {
                if (!updateExceptionLogged)
                {
                    updateExceptionLogged = true;
                    Mod.logger.LogError($"[SonarWorld] Update exception: {e}");
                }
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
            swappedRenderers.Clear();
            originalMaterials.Clear();
            gridMaterials.Clear();
            gridBaseColors.Clear();
            gridSlots.Clear();
            swapped = false;
            hiddenFrames = 0;
        }

        static void CreateRing(Vector3 position, Vector3 camForward)
        {
            var shader = AdditiveShader();
            if (shader == null) return;
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
            var c = gridBaseColors.Count > 0 ? gridBaseColors[0] : FallbackPingColor;
            ringMat.color = c;
            ringMat.renderQueue = 3100;
            ringBaseAlpha = c.a;
            var ringRenderer = ringGo.GetComponent<Renderer>();
            if (ringRenderer != null)
            {
                ringRenderer.sharedMaterial = ringMat;
            }
            ringGo.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        }

        static void DestroyRing()
        {
            if (ringMat != null)
            {
                Object.Destroy(ringMat);
            }
            if (ringGo != null)
            {
                Object.Destroy(ringGo);
            }
            ringGo = null;
            ringMat = null;
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

        // The runtime Shader.Find database does not include the additive
        // particle shader (the log shows it still returns Sprites/Default
        // while the ping's particle already uses it): the shader is loaded
        // by the particle's material reference, not the find database. The
        // ping itself spawns the ObjectivePing particle, so adopt the
        // shader from its material a few frames later. Runs on every
        // ping/rescan until the shader is adopted, then is a no-op
        static void TryAdoptAdditiveShader()
        {
            if (adoptedShader != null) return;
            if (Time.frameCount - lastAdoptFrame < 30) return;
            lastAdoptFrame = Time.frameCount;
            var go = GameObject.Find("ObjectivePing");
            if (go == null) return;
            var rend = go.GetComponent<Renderer>();
            if (rend == null) return;
            var mats = rend.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m != null && m.shader != null && m.shader.name != null
                    && m.shader.name.ToLowerInvariant().IndexOf("additive") >= 0)
                {
                    adoptedShader = m.shader;
                    if (additiveShaderName != adoptedShader.name)
                    {
                        additiveShaderName = adoptedShader.name;
                        Mod.logger.LogInfo($"[SonarWorld] additive shader={adoptedShader.name} (adopted from ObjectivePing)");
                    }
                    return;
                }
            }
        }

        // The probe runs on every ping/rescan (the result is not cached):
        // the first ping may happen before the additive shader is available
        // (adopted from the ObjectivePing particle, see above)
        static Shader AdditiveShader()
        {
            if (adoptedShader != null)
            {
                return adoptedShader;
            }
            Shader found = null;
            foreach (var name in AdditiveShaderNames)
            {
                found = Shader.Find(name);
                if (found != null) break;
            }
            if (found != null && additiveShaderName != found.name)
            {
                additiveShaderName = found.name;
                Mod.logger.LogInfo($"[SonarWorld] additive shader={found.name}");
            }
            return found;
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

    // The WBOIT composite pass rebuilds its CommandBuffer from the registered
    // overlays every frame (FillBuffer returns false = the overlay draws
    // nothing this frame). Blocking the sonar overlay removes the per-eye
    // grid while the rest of the pass (temperature refraction, other
    // overlays) keeps working
    [HarmonyPatch(typeof(VFXOverlayMaterial), nameof(VFXOverlayMaterial.FillBuffer))]
    static class SonarOverlayBlock
    {
        static bool blockLogged;
        static float otherOverlayLogTime;

        [HarmonyPrefix]
        static bool Prefix(VFXOverlayMaterial __instance, out bool __result)
        {
            __result = true;
            var mat = __instance != null ? __instance.material : null;
            if (mat == null || mat.shader == null || mat.shader.name == null)
            {
                return true;
            }
            if (mat.shader.name.ToLowerInvariant().IndexOf("sonar") < 0)
            {
                // Diagnostics: list the other overlays (the Seamoth sonar
                // might use a different name) - throttled, debug only
                if (Settings.IsDebugEnabled && Time.unscaledTime - otherOverlayLogTime > 60f)
                {
                    otherOverlayLogTime = Time.unscaledTime;
                    Mod.logger.LogInfo($"[SonarWorld] overlay pass (not blocked): mat={mat.name} shader={mat.shader.name}");
                }
                return true;
            }
            if (!blockLogged)
            {
                blockLogged = true;
                Mod.logger.LogInfo($"[SonarWorld] overlay blocked: mat={mat.name} shader={mat.shader.name}");
            }
            __result = false;
            return false;
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

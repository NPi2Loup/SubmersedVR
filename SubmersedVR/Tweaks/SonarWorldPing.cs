using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SubmersedVR
{
    // World-anchored expanding wave ring on sonar pings. The game's sonar
    // hologram (the Cyclops' radar disc + mini model) is left untouched:
    // the camera-following grid the user sees is NOT drawn by its meshes
    // (confirmed in-game: swapping all of them changed nothing) nor by a
    // WBOIT overlay while it was visible. SonarOverlayBlock below stays as
    // a probe: if the grid is a WBOIT overlay (like the scanner's
    // FX/Scanning one), the log will name it on the next Seamoth test.
    static class SonarWorldPing
    {
        private const float MaxRadius = 10f;
        private static readonly Color FallbackPingColor = new Color(0.253f, 0.593f, 0.662f, 0.196f);
        private static readonly string[] AdditiveShaderNames =
        {
            "Legacy Shaders/Particles/Additive",
            "Particles/Additive",
            "Sprites/Default",
            "UWE/Standard",
            "Universal Render Pipeline/Particles/Unlit",
            "Hidden/Universal Default"
        };

        static string additiveShaderName;
        static Texture2D ringTexture;
        static bool updateExceptionLogged;

        // Ping wave (ring) state
        static bool hasPinged;
        static float ringStartTime;
        static float duration;
        internal static float waveDuration = 5f;
        static Vector3 origin;
        static GameObject ringGo;
        static Material ringMat;
        static float ringBaseAlpha;

        // True while the ping wave is running: the game's screen effect
        // (red grid + object outlines, the actual sonar readout) is only
        // allowed during the ping, not persistently between pings
        internal static bool IsPinging
        {
            get
            {
                return hasPinged && Time.time - ringStartTime < duration;
            }
        }

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

            try
            {
                CreateRing(camTransform.position, camTransform.forward);
                Mod.logger.LogInfo($"[SonarWorld] ping at origin={origin}");
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

        public static void Update()
        {
            if (Mod.quitting) return;
            try
            {
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
            ringMat.color = FallbackPingColor;
            ringMat.renderQueue = 3100;
            ringBaseAlpha = FallbackPingColor.a;
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

        // Probed on every ping (not cached): the first ping may happen
        // before the additive shaders are loaded in-game
        static Shader AdditiveShader()
        {
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

    // Probe on the WBOIT composite pass: logs every overlay that reaches the
    // pass (and blocks the ones whose shader is sonar-named, in case the
    // camera-following grid is one - the Seamoth sonar test will tell).
    // The scanner's FX/Scanning overlay confirmed the mechanism exists.
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
                // Diagnostics: list the other overlays the pass sees
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

    // The game draws the sonar grid + object outlines + ping wave as a
    // screen-space image effect (OnRenderImage, "Image Effects/Sonar") over
    // the whole stereo frame. Its vanishing point sits at the frame center -
    // between the two eyes - so each eye sees it offset: the "double grid
    // that follows the head" artifact. Postfix on the effect:
    //  - between pings: the whole effect is erased (blit the clean image)
    //  - during a ping: the effect is kept on ONE eye only (the clean right
    //    half of the frame is blitted back over the effect), which removes
    //    the double-grid artifact: the red grid + object outlines (the actual
    //    sonar readout) stay, visible in the left eye
    [HarmonyPatch(typeof(SonarScreenFX), nameof(SonarScreenFX.OnRenderImage))]
    static class SonarScreenFXBlock
    {
        static bool logged;
        static bool eraseFailed;
        static CommandBuffer eraseBuffer;
        static Mesh eraseMesh;
        static Material eraseMaterial;

        [HarmonyPostfix]
        static void Postfix(RenderTexture source, RenderTexture destination)
        {
            if (!logged)
            {
                logged = true;
                Mod.logger.LogInfo($"[SonarWorld] screen sonar FX: ping-only, eye={Settings.SonarPingEye}");
            }
            if (source == null || destination == null)
            {
                return;
            }
            if (!SonarWorldPing.IsPinging)
            {
                Graphics.Blit(source, destination);
                return;
            }
            // Both eyes: keep the game's full (stereo-artifacted) display
            if (Settings.SonarPingEye == "Both Eyes" || eraseFailed)
            {
                return;
            }
            // One eye: draw the clean source over the other half of the
            // stereo frame (left eye = left half, right eye = right half)
            if (eraseMaterial == null)
            {
                var shader = FindShader("Unlit/Texture", "Unlit/Transparent", "Sprites/Default");
                if (shader == null)
                {
                    eraseFailed = true;
                    Mod.logger.LogInfo("[SonarWorld] one-eye erase unavailable (no blit shader), keeping both eyes");
                    return;
                }
                eraseMaterial = new Material(shader);
                eraseMesh = BuildQuad();
                eraseBuffer = new CommandBuffer();
            }
            eraseMaterial.mainTexture = source;
            float tx = Settings.SonarPingEye == "Right Eye" ? -0.5f : 0.5f;
            eraseBuffer.Clear();
            eraseBuffer.SetRenderTarget(destination);
            eraseBuffer.DrawMesh(eraseMesh, Matrix4x4.TRS(new Vector3(tx, 0f, 0f), Quaternion.identity, new Vector3(0.5f, 1f, 1f)), eraseMaterial);
            Graphics.ExecuteCommandBuffer(eraseBuffer);
        }

        static Shader FindShader(params string[] names)
        {
            foreach (var name in names)
            {
                var found = Shader.Find(name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        // Full NDC quad: DrawMesh scales it to half width and offsets it to
        // the half of the stereo frame that must be erased
        static Mesh BuildQuad()
        {
            var m = new Mesh();
            m.name = "SonarEraseQuad";
            m.vertices = new Vector3[]
            {
                new Vector3(-1f, -1f, 0f),
                new Vector3(1f, -1f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(-1f, 1f, 0f)
            };
            m.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };
            m.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            return m;
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

using System.IO;
using UnityEngine;

namespace SubmersedVR
{
    // The single "Sonar screen effect" selection (replaces the old v32 /
    // v32.1 toggles, the fix-mode choice and the Sonar Ping Eye control).
    //
    //   Original (game)                  -> the game's effect, ping-only, both eyes
    //   Original (recompiled by us)      -> CONTROL: the game's effect verbatim,
    //                                        recompiled in our bundle and launched
    //                                        by our code (ping-only like the game
    //                                        original) - isolates our launch path
    //   Blue wave + trail (experimental) -> a bright lagoon-blue sonar wave,
    //                                        additive (SonarScreenWave)
    //   Edges reveal (experimental)      -> screen-space mesh edges + silhouettes
    //                                        lit by the same ping front (SonarScreenEdges)
    //   Stereo fix: per-eye matrices     -> the game's effect, stereo-corrected (SonarScreenShaderFixV2, mode 2)
    //   Stereo fix: per-eye C2W only     -> (mode 1, diagnostic)
    //   Stereo fix: translation          -> (mode 0, the v32 prototype)
    static class SonarEffectOptions
    {
        // v69: the user's final labels for the three offered options
        public const string Original = "legacy";
        public const string OriginalRecompiled = "Original (recompiled by us)";
        public const string Wave = "blue wave";
        public const string Edges = "Edges reveal (experimental)";
        public const string FixPerEyeMatrices = "legacy (3D fixed)";
        public const string FixPerEyeRework = "Stereo fix: per-eye matrices (rework)";
        public const string FixPerEyeC2W = "Stereo fix: per-eye C2W only";
        public const string FixTranslation = "Stereo fix: translation (prototype)";

        public static readonly string[] All =
        {
            Original,
            OriginalRecompiled,
            Wave,
            Edges,
            FixPerEyeMatrices,
            FixPerEyeRework,
            FixPerEyeC2W,
            FixTranslation
        };

        // v63: the options OFFERED in the app. The rest (recompiled control,
        // edges, rework, C2W-only, translation) stay fully in the code but
        // are not selectable anymore - their shaders may be dropped from
        // the bundle (the drivers stay inert when a shader is missing)
        public static readonly string[] Visible =
        {
            Original,
            Wave,
            FixPerEyeMatrices
        };

        public const string V2ShaderName = "SubmersedVR/SonarScreenStereoV2";
        // The rework grid: the V2 shader with the x=c lattice family
        // dropped (no more wedges converging on the vanishing point)
        public const string V2ReworkShaderName = "SubmersedVR/SonarScreenStereoV2Rework";
        public const string WaveShaderName = "SubmersedVR/SonarScreenWave";
        public const string EdgesShaderName = "SubmersedVR/SonarScreenEdges";
        // The legacy shader is a verbatim port of the game's effect; with
        // zero eye offsets (its material defaults) it IS the recompiled
        // original
        public const string LegacyShaderName = "SubmersedVR/SonarScreenStereo";

        public static bool IsWave(string s)
        {
            return s == Wave;
        }

        public static bool IsEdges(string s)
        {
            return s == Edges;
        }

        public static bool IsRecompiled(string s)
        {
            return s == OriginalRecompiled;
        }

        public static bool IsFix(string s)
        {
            return s == FixPerEyeMatrices || s == FixPerEyeRework || s == FixPerEyeC2W || s == FixTranslation;
        }

        public static bool IsRework(string s)
        {
            return s == FixPerEyeRework;
        }

        // A replacement shader takes over the whole screen effect. The
        // recompiled control is NOT a replacement: it keeps the ping-only
        // erase of the "Original (game)" selection so the A/B comparison
        // only differs in shader + launch path
        public static bool IsReplacement(string s)
        {
            return IsWave(s) || IsEdges(s) || IsFix(s);
        }

        // v32.1 fix mode for the stereo-fix selections
        public static float FixMode(string s)
        {
            if (s == FixTranslation)
            {
                return 0f;
            }
            if (s == FixPerEyeC2W)
            {
                return 1f;
            }
            return 2f;
        }

        // Re-apply whichever module owns the selection (swap/restore the
        // effect's material) and report the state in the log
        public static void ApplySelection()
        {
            SonarScreenShaderFixV2.ApplyState();
            SonarScreenWave.ApplyState();
            SonarScreenEdges.ApplyState();
            LogStatus();
        }

        // One startup status line so the log always shows what the option
        // will do (and whether the bundle/shader is actually present)
        static bool startupLogged;

        // v63/v69: migrate a PERSISTED selection that is no longer offered
        // in the app (old labels, edges, rework, recompiled, diag modes) to
        // one of the three current options, so the choice UI and the
        // drivers stay coherent. The old options' code is untouched
        public static void SanitizeSelection()
        {
            string sel = Settings.SonarScreenEffect;
            string migrated = null;
            if (sel == "Original (game)") migrated = Original;
            else if (sel == "Original (recompiled by us)") migrated = Original;
            else if (sel == "Blue wave + trail (experimental)") migrated = Wave;
            else if (sel == "Stereo fix: per-eye matrices") migrated = FixPerEyeMatrices;
            else if (sel == "Stereo fix: per-eye matrices (rework)") migrated = FixPerEyeMatrices;
            else if (sel == "Stereo fix: per-eye C2W only") migrated = FixPerEyeMatrices;
            else if (sel == "Stereo fix: translation (prototype)") migrated = FixPerEyeMatrices;
            else if (sel == "Edges reveal (experimental)") migrated = Wave;
            if (migrated != null)
            {
                Mod.logger.LogInfo($"[SonarFX] saved screen effect '{sel}' migrated to '{migrated}'");
                Settings.SonarScreenEffect = migrated;
                return;
            }
            if (System.Array.IndexOf(Visible, sel) < 0)
            {
                Mod.logger.LogInfo($"[SonarFX] saved screen effect '{sel}' is no longer offered - falling back to '{Wave}'");
                Settings.SonarScreenEffect = Wave;
            }
        }

        // v69: the master toggle was removed from the app - the mod is
        // always on, and the "legacy" selection IS the 100% original
        // behavior (game screen effect, no world ping ring). Force it on
        // so a persisted "off" cannot leave the mod stuck disabled with
        // no way to re-enable it from the menu
        public static void SanitizeModEnabled()
        {
            if (!Settings.SonarModEnabled)
            {
                Mod.logger.LogInfo("[SonarFX] master toggle removed in v69 - forcing the sonar mod on (the 'legacy' selection = 100% original behavior)");
                Settings.SonarModEnabled = true;
            }
        }

        // Idempotent: keeps the persisted values on the current option set.
        // v70: must run BEFORE the options panel builds its rows (the panel
        // is created at menu load, before the VR startup hook) - with a
        // persisted old label the sonar choice row would vanish. Also
        // called from the settings load/save postfix
        public static void Sanitize()
        {
            SanitizeModEnabled();
            SanitizeSelection();
        }

        public static void OnStartup()
        {
            if (startupLogged)
            {
                return;
            }
            startupLogged = true;
            Sanitize();
            LogStatus();
        }

        public static void LogStatus()
        {
            string sel = Settings.SonarScreenEffect;
            string state;
            if (!Settings.SonarModEnabled)
            {
                state = "master off = 100% original";
            }
            else if (!IsReplacement(sel) && !IsRecompiled(sel))
            {
                state = "original game screen effect (ping-only)";
            }
            else
            {
                string shaderName = IsWave(sel) ? WaveShaderName : (IsEdges(sel) ? EdgesShaderName : (IsRecompiled(sel) ? LegacyShaderName : (IsRework(sel) ? V2ReworkShaderName : V2ShaderName)));
                state = SonarBundle.GetShader(shaderName) != null ? "shader loaded" : "inert (sonar_resources bundle or shader missing)";
            }
            Mod.logger.LogInfo($"[SonarFX] screen effect: {sel} ({state})");
        }
    }

    // Loads the sonar_resources asset bundle once and serves the shaders to
    // both screen-effect modules (the stereo fix and the wave). The bundle is
    // unloaded only on application quit.
    static class SonarBundle
    {
        private const string BundleName = "sonar_resources";

        static AssetBundle bundle;
        static bool loadFailed;
        static bool missingLogged;

        public static bool IsLoaded
        {
            get { return bundle != null; }
        }

        public static Shader GetShader(string shaderName)
        {
            if (bundle == null && !loadFailed)
            {
                string path = Path.Combine(Application.streamingAssetsPath, BundleName);
                if (!File.Exists(path))
                {
                    loadFailed = true;
                    if (!missingLogged)
                    {
                        missingLogged = true;
                        Mod.logger.LogInfo("[SonarFX] sonar_resources bundle not found in StreamingAssets - screen effect options are inert (the original game effect is kept)");
                    }
                    return null;
                }
                try
                {
                    bundle = AssetBundle.LoadFromFile(path);
                }
                catch (System.Exception e)
                {
                    loadFailed = true;
                    Mod.logger.LogError($"[SonarFX] sonar bundle load failed: {e}");
                    return null;
                }
            }
            if (bundle == null)
            {
                return null;
            }
            foreach (var a in bundle.LoadAllAssets<Shader>())
            {
                if (a != null && a.name == shaderName)
                {
                    return a;
                }
            }
            return null;
        }

        public static void OnQuit()
        {
            if (bundle != null)
            {
                bundle.Unload(false);
                bundle = null;
            }
        }
    }
}

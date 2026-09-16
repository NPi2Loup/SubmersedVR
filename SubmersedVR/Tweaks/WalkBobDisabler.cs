using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // The game applies the walking camera bobbing (vertical camera movement) to the
    // main camera, which the VR rig steals, so the bob goes straight into the eyes.
    // Cutting it at the source preserves VR comfort.
    static class WalkBobDisabler
    {
        public static void Apply()
        {
            MiscSettings.cameraBobbing = !Settings.DisableWalkBobbing;
        }
    }

    // The game can reload the setting after the rig setup, so force it each frame while the option is on
    class WalkBobEnforcer : MonoBehaviour
    {
        void Update()
        {
            if (Settings.DisableWalkBobbing)
            {
                MiscSettings.cameraBobbing = false;
            }
        }
    }

    // The rig is set up after the game init, so re-apply the option there.
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class WalkBobDisablerRigSetup
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            WalkBobDisabler.Apply();
            Mod.logger.LogInfo($"[WalkBob] applied cameraBobbing={MiscSettings.cameraBobbing}");
            __instance.gameObject.GetOrAddComponent<WalkBobEnforcer>();
        }
    }
}

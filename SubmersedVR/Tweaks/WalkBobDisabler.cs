using HarmonyLib;

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

    // The rig is set up after the game init, so re-apply the option there.
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class WalkBobDisablerRigSetup
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            WalkBobDisabler.Apply();
        }
    }
}

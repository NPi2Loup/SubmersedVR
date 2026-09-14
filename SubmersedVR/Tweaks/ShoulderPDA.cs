using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    extern alias SteamVRRef;
    extern alias SteamVRActions;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;

    /// <summary>
    /// Reach your left hand to the PDA zone (shoulder or lower back) and press the left
    /// trigger to open/close the PDA. The gesture is emulated as a press of the game's
    /// PDA button, so the regular PDA input path and its Steam Input bindings keep working.
    /// While the left hand is in the zone, MoveDown and Sprint inputs are suppressed
    /// so gripping doesn't make you swim down.
    /// </summary>
    public class ShoulderPDA : MonoBehaviour
    {
        // Cooldown between PDA toggles to prevent rapid open/close
        private const float ToggleCooldown = 0.6f;

        private float lastToggleTime = -1f;
        private bool wasInZone = false;

        // Updated every frame, read by the input patches in SteamVrGameInput
        internal static bool IsHandInPDAZone = false;

        private static bool pendingPDAButtonPress;

        internal static bool ConsumePendingPDAButtonPress()
        {
            bool pressed = pendingPDAButtonPress;
            pendingPDAButtonPress = false;
            return pressed;
        }

        void Update()
        {
            IsHandInPDAZone = false;
            if (!Settings.ShoulderPDA) return;

            var rig = VRCameraRig.instance;
            if (rig == null || rig.leftController == null || rig.vrCamera == null || Player.main == null) return;

            Transform head = rig.vrCamera.transform;

            // Shoulder: left, down and slightly behind the head. Lower back: left hip/belt.
            Vector3 zoneCenter;
            float zoneRadius;
            if (Settings.PDAReachZone == "Lower Back")
            {
                zoneCenter = head.position + head.right * -0.25f + Vector3.down * 0.6f + head.forward * -0.15f;
                zoneRadius = 0.25f;
            }
            else
            {
                zoneCenter = head.position + head.right * -0.2f + Vector3.down * 0.2f + head.forward * -0.2f;
                zoneRadius = 0.2f;
            }

            IsHandInPDAZone = Vector3.Distance(rig.leftController.transform.position, zoneCenter) <= zoneRadius;

            // Subtle haptic when the hand enters the zone
            if (IsHandInPDAZone && !wasInZone)
            {
                HapticsVR.PlayGameHaptics(HapticsVR.Controller.Left, 0.0f, 0.08f, 10f, 0.3f);
            }
            wasInZone = IsHandInPDAZone;
            if (!IsHandInPDAZone) return;
            // The gesture must also work while the PDA is open (to close it again),
            // but IsFreeToInteract is false in that state
            bool pdaOpen = Player.main.GetPDA()?.isInUse == true;
            if (!pdaOpen && !Player.main.IsFreeToInteract()) return;

            // Left trigger (LeftHand action), not the grip, since the grip is bound to MoveDown/Sprint
            bool triggerDown = SteamVR_Actions.subnautica.LeftHand.GetStateDown(SteamVR_Input_Sources.LeftHand);
            if (!triggerDown) return;

            if (Time.time - lastToggleTime < ToggleCooldown) return;
            lastToggleTime = Time.time;

            // Emulate a press of the game's PDA button, consumed by the GetButtonDown prefix
            pendingPDAButtonPress = true;
            HapticsVR.PlayGameHaptics(HapticsVR.Controller.Left, 0.0f, 0.15f, 15f, 0.6f);
        }

        void OnDestroy()
        {
            IsHandInPDAZone = false;
        }
    }

    #region Patches

    // Attach ShoulderPDA to the camera rig
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class AttachShoulderPDA
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            __instance.gameObject.GetOrAddComponent<ShoulderPDA>();
        }
    }

    #endregion
}

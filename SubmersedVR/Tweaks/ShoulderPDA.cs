using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    extern alias SteamVRRef;
    extern alias SteamVRActions;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;

    /// <summary>
    /// Reach your left hand to the PDA zone (shoulder or hip) and press the left
    /// trigger to open/close the PDA. The open gesture is emulated as a press of the
    /// game's PDA button so the regular PDA input path keeps working; the close
    /// gesture calls PDA.Close directly since the game's PDA button path does not
    /// reach it while the PDA is open.
    /// </summary>
    public class ShoulderPDA : MonoBehaviour
    {
        // Cooldown between PDA toggles to prevent rapid open/close. Uses unscaled
        // time: with the "pause game when PDA open" option the scaled clock freezes
        // while the PDA is open, which would make the close gesture wait forever
        private const float ToggleCooldown = 0.6f;

        private float lastToggleTime = -1f;
        private bool wasInZone = false;

        private static bool pendingPDAButtonPress;

        internal static bool ConsumePendingPDAButtonPress()
        {
            bool pressed = pendingPDAButtonPress;
            pendingPDAButtonPress = false;
            return pressed;
        }

        void Update()
        {
            if (!Settings.ShoulderPDA) return;

            var rig = VRCameraRig.instance;
            if (rig == null || rig.leftController == null || rig.vrCamera == null || Player.main == null) return;

            Transform head = rig.vrCamera.transform;

            // Shoulder: left, down and slightly behind the head. Hip: left hand resting on the left hip.
            Vector3 zoneCenter;
            float zoneRadius;
            if (Settings.PDAReachZone == "Hip")
            {
                zoneCenter = head.position + head.right * -0.20f + Vector3.down * 0.55f + head.forward * -0.05f;
                zoneRadius = 0.09f;
            }
            else
            {
                zoneCenter = head.position + head.right * -0.2f + Vector3.down * 0.2f + head.forward * -0.2f;
                zoneRadius = 0.06f;
            }

            bool inZone = Vector3.Distance(rig.leftController.transform.position, zoneCenter) <= zoneRadius;

            // Subtle haptic when the hand enters the zone
            if (inZone && !wasInZone)
            {
                HapticsVR.PlayGameHaptics(HapticsVR.Controller.Left, 0.0f, 0.08f, 10f, 0.3f);
            }
            wasInZone = inZone;
            if (!inZone) return;
            // The gesture must also work while the PDA is open (to close it again),
            // but IsFreeToInteract is false in that state
            var pda = Player.main.GetPDA();
            bool pdaOpen = pda != null && (pda.state == PDA.State.Opened || pda.state == PDA.State.Opening);
            if (!pdaOpen && !Player.main.IsFreeToInteract()) return;

            // Left trigger (LeftHand action)
            bool triggerDown = SteamVR_Actions.subnautica.LeftHand.GetStateDown(SteamVR_Input_Sources.LeftHand);
            if (!triggerDown) return;

            Mod.logger.LogInfo($"[ShoulderPDA] trigger in zone: state={pda?.state} isInUse={pda?.isInUse} isOpen={pda?.isOpen} freeToInteract={Player.main.IsFreeToInteract()}");

            if (Time.unscaledTime - lastToggleTime < ToggleCooldown) return;
            lastToggleTime = Time.unscaledTime;

            if (pdaOpen)
            {
                // The game's PDA button path does not reach the close while the PDA is open,
                // so close it directly (the fork's proven approach)
                Mod.logger.LogInfo("[ShoulderPDA] closing PDA");
                pda.Close();
            }
            else
            {
                // Emulate a press of the game's PDA button, consumed by the GetButtonDown prefix
                Mod.logger.LogInfo("[ShoulderPDA] opening PDA (virtual press)");
                pendingPDAButtonPress = true;
            }
            HapticsVR.PlayGameHaptics(HapticsVR.Controller.Left, 0.0f, 0.15f, 15f, 0.6f);
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

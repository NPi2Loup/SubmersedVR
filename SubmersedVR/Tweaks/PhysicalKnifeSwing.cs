using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    extern alias SteamVRRef;
    extern alias SteamVRActions;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;

    /// <summary>
    /// Detects physical swing gestures from the right VR controller and triggers
    /// knife attacks based on swing velocity. Works with both Knife and HeatBlade.
    /// While enabled, the trigger button is disabled for the knife so it stays free.
    /// </summary>
    public class PhysicalKnifeSwing : MonoBehaviour
    {
        // Minimum time between swings to prevent spam
        private const float SwingCooldown = 0.4f;

        // Scale haptic intensity by swing speed (clamped)
        private const float MaxHapticSpeed = 5.0f;

        private float lastSwingTime = -1f;
        private SteamVR_Behaviour_Pose rightControllerPose;
        private bool wasAboveThreshold = false;

        public static PhysicalKnifeSwing instance;

        // Set true only during our own call to OnToolUseAnim so the prefix patch lets it through
        public bool IsSwinging { get; private set; }

        void Awake()
        {
            instance = this;
        }

        void Start()
        {
            // Get the pose component from the right controller
            var rig = VRCameraRig.instance;
            if (rig != null && rig.rightController != null)
            {
                rightControllerPose = rig.rightController.GetComponent<SteamVR_Behaviour_Pose>();
            }
        }

        void Update()
        {
            if (!Settings.PhysicalKnifeSwing) return;
            if (rightControllerPose == null) return;
            if (Player.main == null) return;
            if (!Player.main.IsFreeToInteract()) return;
            if (Player.main.pda != null && Player.main.pda.isOpen) return;

            // Only trigger when holding a Knife (HeatBlade extends Knife)
            var heldTool = Inventory.main?.GetHeldTool();
            if (!(heldTool is Knife knife)) return;

            float speed = rightControllerPose.GetVelocity().magnitude;
            bool isAboveThreshold = speed >= Settings.KnifeSwingSpeedThreshold;

            // Trigger on the rising edge: speed crosses above threshold
            if (isAboveThreshold && !wasAboveThreshold)
            {
                if (Time.time - lastSwingTime >= SwingCooldown)
                {
                    TriggerSwing(knife, speed);
                }
            }

            wasAboveThreshold = isAboveThreshold;
        }

        private void TriggerSwing(Knife knife, float speed)
        {
            lastSwingTime = Time.time;

            // Get the GUIHand to pass to OnToolUseAnim
            var guiHand = Player.main?.GetComponent<GUIHand>();
            if (guiHand == null) return;

            // Fire the knife attack directly
            IsSwinging = true;
            knife.OnToolUseAnim(guiHand);
            IsSwinging = false;

            // Haptic feedback scaled by swing speed
            HapticsVR.PlayGameHaptics(HapticsVR.Controller.Right, 0.0f, 0.15f, 20f, Mathf.Clamp01(speed / MaxHapticSpeed));
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }

    #region Patches

    // Attach PhysicalKnifeSwing to the VRCameraRig when it's created
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class AttachPhysicalKnifeSwing
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            __instance.gameObject.GetOrAddComponent<PhysicalKnifeSwing>();
        }
    }

    // Block the knife attack when it didn't come from the physical swing,
    // so the trigger button does nothing with the knife equipped.
    [HarmonyPatch(typeof(Knife), nameof(Knife.OnToolUseAnim))]
    static class SuppressButtonKnifeAttack
    {
        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!Settings.PhysicalKnifeSwing) return true;

            // Only allow the call if it came from our physical swing
            if (PhysicalKnifeSwing.instance != null && PhysicalKnifeSwing.instance.IsSwinging)
                return true;

            return false;
        }
    }

    // Block OnRightHandDown so the trigger does nothing at all with the knife equipped.
    // This prevents the attack animation from starting and frees the trigger for other uses.
    [HarmonyPatch(typeof(PlayerTool), nameof(PlayerTool.OnRightHandDown))]
    static class SuppressKnifeTrigger
    {
        [HarmonyPrefix]
        static bool Prefix(PlayerTool __instance, ref bool __result)
        {
            if (!Settings.PhysicalKnifeSwing) return true;
            if (!(__instance is Knife)) return true;

            __result = false;
            return false;
        }
    }

    #endregion
}

using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    extern alias SteamVRRef;
    extern alias SteamVRActions;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;

    /// <summary>
    /// Detects physical swing gestures from the right VR controller. The velocity
    /// threshold only arms a short detection window; the attack fires when a
    /// hittable target enters the probe hitbox (mirroring the game's trace), so a
    /// big gesture "waits" for the target to come into the swing path. If the
    /// gesture ends without a target (speed drops or the window expires), a whiff
    /// is fired so the swing still gets its animation and sound.
    /// While enabled, the trigger button no longer attacks with the knife.
    /// </summary>
    public class PhysicalKnifeSwing : MonoBehaviour
    {
        private enum SwingState
        {
            Idle,
            Armed,
            Fired
        }

        // Safety timeout in the Fired state, in case the hand keeps moving
        // above the threshold (no discrete gesture end)
        private const float FiredSafetyTimeout = 1.0f;

        // Inflates the probe radius relative to the game's trace radius, to cover
        // the hand-to-aim offset and tracking noise. Shared with KnifeHitboxDebug.
        internal const float ProbeRadiusScale = 1.5f;

        // How long a swing stays armed waiting for a target before firing a whiff
        private const float SwingWindow = 0.2f;

        // Scale haptic intensity by swing speed (clamped)
        private const float MaxHapticSpeed = 5.0f;

        // Must match PlayerTool.TraceForTarget's default sphere radius.
        // Shared with KnifeHitboxDebug.
        internal const float TraceSphereRadius = 0.2f;

        // The probe hit list is reused across frames
        private static readonly RaycastHit[] capsuleHits = new RaycastHit[16];

        private float firedTime = -1f;
        private float armTime = -1f;
        private SteamVR_Behaviour_Pose rightControllerPose;
        private bool wasAboveThreshold = false;
        private SwingState state = SwingState.Idle;

        public static PhysicalKnifeSwing instance;

        // Set true only during our own call to OnToolUseAnim so the prefix patch lets it through
        public bool IsSwinging { get; private set; }

        // Time of the last fired swing, read by KnifeHitboxDebug to flash the hitbox
        internal static float LastSwingTime = -1f;

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
            if (!Settings.PhysicalKnifeSwing)
            {
                ResetToIdle();
                return;
            }
            if (rightControllerPose == null) return;
            if (Player.main == null || !Player.main.IsFreeToInteract())
            {
                ResetToIdle();
                return;
            }
            if (Player.main.pda != null && Player.main.pda.isOpen)
            {
                ResetToIdle();
                return;
            }

            // Only active when holding a Knife (HeatBlade extends Knife)
            if (!(Inventory.main?.GetHeldTool() is Knife knife))
            {
                ResetToIdle();
                return;
            }

            Vector3 velocity = rightControllerPose.GetVelocity();
            float speed = velocity.magnitude;
            bool isAboveThreshold = speed >= Settings.KnifeSwingSpeedThreshold;

            switch (state)
            {
                case SwingState.Idle:
                    // Rising edge: the gesture starts, arm the detection window
                    if (isAboveThreshold && !wasAboveThreshold)
                    {
                        state = SwingState.Armed;
                        armTime = Time.time;
                    }
                    break;

                case SwingState.Armed:
                    // Fire as soon as a hittable target is in the probe hitbox,
                    // while the hand moves toward the aim (not on the pull-back)
                    var aim = Aiming.GetAimTransform();
                    bool movingTowardAim = aim == null || Vector3.Dot(velocity, aim.forward) > -0.2f;
                    if (movingTowardAim && ProbeHittableTarget(knife))
                    {
                        FireSwing(knife, speed);
                        state = SwingState.Fired;
                        firedTime = Time.time;
                    }
                    // The gesture ends without a target: speed drops back below
                    // the threshold, or the detection window expires -> whiff
                    else if (!isAboveThreshold || Time.time - armTime >= SwingWindow)
                    {
                        FireSwing(knife, speed);
                        // Speed already dropped: the gesture is over, rearm right away
                        state = !isAboveThreshold ? SwingState.Idle : SwingState.Fired;
                        firedTime = Time.time;
                    }
                    break;

                case SwingState.Fired:
                    // Rearm when the hand slows down (end of the gesture), so a
                    // back-and-forth swing can hit on every forward pass
                    if (!isAboveThreshold || Time.time - firedTime >= FiredSafetyTimeout)
                    {
                        state = SwingState.Idle;
                    }
                    break;
            }

            wasAboveThreshold = isAboveThreshold;
        }

        // Probe: is there a valid knife target inside the current probe hitbox?
        // The capsule mirrors the game's own trace (0.2 m sphere radius swept for
        // the attack distance along the aim, inflated by the probe radius scale to
        // cover the hand-to-aim offset and tracking noise), so a trigger means
        // the game's trace will hit.
        static bool ProbeHittableTarget(Knife knife)
        {
            var rig = VRCameraRig.instance;
            var aim = Aiming.GetAimTransform();
            if (rig == null || rig.rightController == null || aim == null) return false;

            Vector3 origin = rig.rightController.transform.position;
            float scale = ProbeRadiusScale;
            float length = Mathf.Max(knife.attackDist, TraceSphereRadius * 2f);

            int count = Physics.SphereCastNonAlloc(origin, TraceSphereRadius * scale, aim.forward, capsuleHits, length, ~0);
            for (int i = 0; i < count; i++)
            {
                if (IsHittable(capsuleHits[i].collider)) return true;
            }
            return false;
        }

        // A collider is hittable if it belongs to a valid knife target that is not the player
        static bool IsHittable(Collider collider)
        {
            if (collider == null) return false;
            var go = collider.attachedRigidbody != null ? collider.attachedRigidbody.gameObject : collider.gameObject;
            var live = go.GetComponentInParent<LiveMixin>();
            if (live == null) return false;
            if (live.gameObject == Player.main.gameObject) return false;
            return Knife.IsValidTarget(live);
        }

        private void ResetToIdle()
        {
            state = SwingState.Idle;
            wasAboveThreshold = false;
        }

        private void FireSwing(Knife knife, float speed)
        {
            LastSwingTime = Time.time;

            // Fire the knife attack directly. The flag guards the SuppressButtonKnifeAttack
            // prefix; try/finally so an exception in the game code cannot leak it.
            var guiHand = Player.main?.guiHand;
            if (guiHand == null) return;

            IsSwinging = true;
            try
            {
                knife.OnToolUseAnim(guiHand);
            }
            finally
            {
                IsSwinging = false;
            }

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

    // Block OnRightHandDown so the trigger does nothing at all with the knife
    // equipped. This also prevents the attack animation from starting.
    [HarmonyPatch(typeof(PlayerTool), nameof(PlayerTool.OnRightHandDown))]
    static class SuppressKnifeTrigger
    {
        [HarmonyPrefix]
        static bool Prefix(PlayerTool __instance)
        {
            if (!Settings.PhysicalKnifeSwing) return true;
            if (!(__instance is Knife)) return true;

            return false;
        }
    }

    #endregion
}

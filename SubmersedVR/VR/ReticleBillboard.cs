using TMPro;
using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle readable in laser pointer mode. The root canvas
    // (tool texts + energy + progress + icons) always stays anchored at the
    // hand; the target action text is split onto a second canvas
    // (ReticleSplit) which is projected on the laser hit point while aiming.
    // Both are billboarded to
    // the view, which fixes the original tool-dependent orientation (e.g. the
    // knife is stored about 90 degrees turned in the hand, which rolled the
    // reticle). The per-frame write must win against the game's own
    // HandReticle.LateUpdate, hence DefaultExecutionOrder(1).
    [DefaultExecutionOrder(1)]
    public class ReticleBillboard : MonoBehaviour
    {
        // Hand anchor, shared with the reticle setups in VRHud
        internal static readonly Vector3 AnchorOffset = new Vector3(0f, 0f, 0.05f);
        internal static readonly Vector3 AnchorScale = new Vector3(0.001f, 0.001f, 0.001f);

        // Projected scale: the original reticle was parented to the pointer dot
        // (localScale 0.03) with a local scale of 0.06, an effective 0.0018;
        // it now hangs off the controller UI (scale 1.0)
        internal static readonly Vector3 TargetScale = new Vector3(0.0018f, 0.0018f, 0.0018f);

        // Debug (Debug Overlays): last logged reticle texts
        static string lastHand;
        static string lastHandSub;
        static string lastUse;
        static string lastUseSub;

        void LateUpdate()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.uiCamera == null || rig.rightControllerUI == null)
            {
                return;
            }
            var anchor = rig.rightControllerUI.transform;
            if (transform.parent != anchor)
            {
                // Not anchored at the hand anymore: hide the split target so it
                // does not freeze at the last hit point
                var frozenTarget = ReticleSplit.TargetCanvas;
                if (frozenTarget != null)
                {
                    frozenTarget.gameObject.SetActive(false);
                }
                return;
            }

            // The root always stays at the hand (tool texts + icons live there)
            transform.localPosition = AnchorOffset;
            transform.localScale = AnchorScale;

            var laser = rig.laserPointerUI;
            bool aiming = rig.HasWorldTarget() && laser != null;

            // The game re-parents the primary action text to its icon every
            // frame: enforce the split back onto the target canvas while
            // aiming; without a world target (e.g. build mode) the action
            // texts must stay on the hand, otherwise the build info would
            // be invisible
            ReticleSplit.Enforce(aiming);

            // Plane perpendicular to the hand laser (world up), like the
            // original mod's laser mode: the hand texts stay parallel to the
            // pointed text, without following the head yaw; facing the view
            // when there is no laser
            if (laser != null)
            {
                transform.rotation = Quaternion.LookRotation(laser.transform.forward, Vector3.up);
            }
            else
            {
                transform.rotation = rig.uiCamera.transform.rotation;
            }

            // Project the split target-info canvas on the hit point while aiming
            var target = ReticleSplit.TargetCanvas;
            if (target != null)
            {
                if (aiming)
                {
                    Vector3 hit = laser.transform.position + laser.transform.forward * rig.worldTargetDistance;
                    target.localPosition = transform.InverseTransformPoint(hit);
                    // Effective world scale TargetScale, relative to the root (AnchorScale)
                    target.localScale = new Vector3(TargetScale.x / AnchorScale.x, TargetScale.y / AnchorScale.y, TargetScale.z / AnchorScale.z);
                    // The root is already on the laser plane: the pointed text
                    // is coplanar with the hand texts
                    target.localRotation = Quaternion.identity;
                    target.gameObject.SetActive(true);
                }
                else
                {
                    target.gameObject.SetActive(false);
                }
            }

            LogTexts();
        }

        // Debug (Debug Overlays): logs the four text fields when they change;
        // the hand/use mapping feeds the planned tool-info/target split
        void LogTexts()
        {
            if (!Settings.IsDebugEnabled) return;
            var handReticle = HandReticle.main;
            if (handReticle == null) return;

            string hand = SafeText(handReticle.compTextHand);
            string handSub = SafeText(handReticle.compTextHandSubscript);
            string use = SafeText(handReticle.compTextUse);
            string useSub = SafeText(handReticle.compTextUseSubscript);
            if (hand == lastHand && handSub == lastHandSub && use == lastUse && useSub == lastUseSub) return;
            lastHand = hand;
            lastHandSub = handSub;
            lastUse = use;
            lastUseSub = useSub;
            Mod.logger.LogInfo($"[ReticleDebug] Hand=\"{hand}\" HandSub=\"{handSub}\" Use=\"{use}\" UseSub=\"{useSub}\"");
        }

        static string SafeText(TextMeshProUGUI comp)
        {
            return comp != null ? comp.text : null;
        }
    }
}

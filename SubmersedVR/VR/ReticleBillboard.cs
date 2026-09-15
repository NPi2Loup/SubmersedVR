using TMPro;
using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle readable in laser pointer mode. Without a target
    // it stays at the hand; while aiming at a target it moves to the laser
    // pointer's hit point (the original pointer dot anchor), so the target
    // text is projected where the action happens. The rotation always faces
    // the view (billboard), which fixes the original tool-dependent
    // orientation (e.g. the knife is stored about 90 degrees turned in the
    // hand, which rolled the reticle). The per-frame write wins against the
    // game's own HandReticle.LateUpdate; DefaultExecutionOrder(1) guarantees
    // we run after it.
    [DefaultExecutionOrder(1)]
    public class ReticleBillboard : MonoBehaviour
    {
        // Hand-mode anchor, shared with the reticle setups in VRHud
        internal static readonly Vector3 AnchorOffset = new Vector3(0f, 0f, 0.05f);
        internal static readonly Vector3 AnchorScale = new Vector3(0.001f, 0.001f, 0.001f);

        // Scale while projected on the target (the original pointer dot reticle scale)
        internal static readonly Vector3 TargetScale = new Vector3(0.06f, 0.06f, 0.06f);

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
                return;
            }

            var laser = rig.laserPointerUI;
            if (rig.HasWorldTarget() && laser != null)
            {
                // Project on the hit point, like the original pointer dot anchor
                transform.position = laser.transform.position + laser.transform.forward * rig.worldTargetDistance;
                transform.localScale = TargetScale;
            }
            else
            {
                // No (recent) target: stay at the hand
                transform.localPosition = AnchorOffset;
                transform.localScale = AnchorScale;
            }

            // Face the view plane: upright text, rolling 1:1 with the head,
            // without tilting when looking up/down
            transform.rotation = rig.uiCamera.transform.rotation;

            LogTexts();
        }

        // Logs the reticle's four text fields when they change, so the
        // tool-info/target split can be worked out from the log
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

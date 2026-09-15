using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle readable in laser pointer mode. The reticle stays
    // at the hand (same anchor and offset as hand mode, see
    // VRHud.SetupHandReticleOnHand); this only re-aims its plane to the view
    // every frame, so the text stays upright and readable no matter how the
    // tool model is held (e.g. the knife is stored about 90 degrees turned in
    // the hand, which rolls the reticle in hand mode). The per-frame write also
    // wins against the game's own HandReticle.LateUpdate, which otherwise
    // re-positions and re-rotates the reticle to its non-VR anchor.
    public class ReticleBillboard : MonoBehaviour
    {
        // Hand-mode anchor offset (see SetupHandReticleOnHand)
        private static readonly Vector3 AnchorOffset = new Vector3(0f, 0f, 0.05f);

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
            transform.localPosition = AnchorOffset;
            // Face the view plane: upright text, rolling 1:1 with the head,
            // without tilting when looking up/down
            transform.rotation = rig.uiCamera.transform.rotation;
        }
    }
}

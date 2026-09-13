using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle (button prompt) facing the HMD so its text stays upright,
    // instead of inheriting the controller pose and the per-tool aim offset.
    // Only enabled in laser pointer mode (see VRHud.SetupHandReticle).
    public class ReticleBillboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.rigParentTarget == null)
            {
                return;
            }
            transform.rotation = rig.rigParentTarget.rotation;
        }
    }
}

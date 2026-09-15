using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle (button prompt) facing the HMD so its text is readable
    // and stays upright with the head, instead of inheriting the controller pose
    // and the per-tool aim offset. Only enabled in laser pointer mode
    // (see VRHud.SetupHandReticle).
    public class ReticleBillboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.uiCamera == null)
            {
                return;
            }
            // The reticle lives in UI/tracking space (under uiRig at the tracking
            // origin) while rigParentTarget is in game world space, so billboard
            // against the UI camera, the HMD proxy in UI space
            var hmd = rig.uiCamera.transform;
            var toHmd = hmd.position - transform.position;
            if (toHmd.sqrMagnitude < 0.0001f)
            {
                return;
            }
            transform.rotation = Quaternion.LookRotation(toHmd, hmd.up);
        }
    }
}

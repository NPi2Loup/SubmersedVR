using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle readable while following the laser dot. The reticle is
    // a direct child of the UI camera (a scale-1, pure-rotation parent, see
    // VRHud.SetupHandReticleLaserPointer), so the position is tracked in local
    // space and the orientation stays exact. Assigning a world rotation while the
    // reticle was under the scaled pointer hierarchy used to shear the quad and
    // show its back. Only enabled in laser pointer mode.
    public class ReticleBillboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.uiCamera == null || rig.laserPointerUI == null)
            {
                return;
            }
            var cam = rig.uiCamera.transform;
            if (transform.parent != cam)
            {
                return;
            }
            Vector3 local = cam.InverseTransformPoint(rig.laserPointerUI.pointerDot.transform.position);
            // Keep a little below the dot, as in the original placement
            local.y -= 0.05f;
            transform.localPosition = local;
            // Face the camera (the player's head) in the camera's own space
            var toCam = -local;
            if (toCam.sqrMagnitude < 0.0001f)
            {
                return;
            }
            transform.localRotation = Settings.ReticleFaceFlip
                ? Quaternion.LookRotation(toCam, cam.up) * Quaternion.Euler(0f, 180f, 0f)
                : Quaternion.LookRotation(toCam, cam.up);
        }
    }
}

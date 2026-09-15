using UnityEngine;
namespace SubmersedVR
{
    // Keeps the hand reticle following the laser dot. The reticle is a direct
    // child of the UI camera (a scale-1, pure-rotation parent, see
    // VRHud.SetupHandReticleLaserPointer), so only its local position is
    // updated per frame; the rotation is fixed at setup (Euler 0,180,0 under
    // the camera), keeping the reticle plane parallel to the view plane:
    // perpendicular to the view, rolling 1:1 with the head, without tilting
    // when looking up/down.
    // The reticle follows the laser dot only while the aim is on a targetable
    // object; otherwise it stays at the hand, as without the laser pointer
    // option. Only active in laser pointer mode.
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
            bool hasTarget = rig.HasWorldTarget();
            Transform anchor = hasTarget ? rig.laserPointerUI.pointerDot.transform : rig.rightControllerUI.transform;
            Vector3 local = cam.InverseTransformPoint(anchor.position);
            // Keep a little below the dot, as in the original placement
            if (hasTarget)
            {
                local.y -= 0.05f;
            }
            transform.localPosition = local;
        }
    }
}

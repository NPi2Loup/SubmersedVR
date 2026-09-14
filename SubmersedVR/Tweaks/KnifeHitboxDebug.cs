using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Debug visual for the knife's melee hitbox: a capsule matching the game's trace
    /// (0.2 m sphere radius, knife attack distance) along a selectable axis
    /// (line of sight, laser pointer aim, or head to hand).
    /// Visible while a Knife/HeatBlade is held and the Debug Overlays option is on.
    /// </summary>
    public class KnifeHitboxDebug : MonoBehaviour
    {
        // Must match PlayerTool.TraceForTarget's default sphere radius
        private const float SphereRadius = 0.2f;

        // How long the hitbox flashes after a swing is fired
        private const float BlinkDuration = 0.2f;

        private static readonly Color VisionColor = new Color(1f, 0.6f, 0f, 0.4f);
        private static readonly Color LaserColor = new Color(0f, 1f, 1f, 0.4f);
        private static readonly Color HeadToHandColor = new Color(1f, 1f, 0f, 0.4f);

        private Renderer visionRenderer;
        private Renderer laserRenderer;
        private Renderer headToHandRenderer;

        void Start()
        {
            visionRenderer = CreateCapsule("KnifeHitboxVision", VisionColor);
            laserRenderer = CreateCapsule("KnifeHitboxLaser", LaserColor);
            headToHandRenderer = CreateCapsule("KnifeHitboxHeadToHand", HeadToHandColor);
        }

        private Renderer CreateCapsule(string name, Color color)
        {
            Material material = new Material(ShaderManager.preloadedShaders.DebugDisplaySolid);
            material.SetColor(ShaderPropertyID._Color, color);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.transform.SetParent(transform, false);
            Destroy(go.GetComponent<CapsuleCollider>());
            var renderer = go.GetComponent<Renderer>();
            renderer.material = material;
            renderer.enabled = false;
            return renderer;
        }

        void Update()
        {
            bool knifeHeld = Inventory.main != null && Inventory.main.GetHeldTool() is Knife;
            bool ready = Settings.IsDebugEnabled
                && VRCameraRig.instance != null && VRCameraRig.instance.vrCamera != null
                && VRCameraRig.instance.laserPointer != null
                && Player.main != null
                && knifeHeld;

            string axis = Settings.KnifeHitboxAxis;
            bool showVision = axis == "Vision" || axis == "Vision + Laser";
            bool showLaser = axis == "Laser Pointer" || axis == "Vision + Laser";
            bool showHeadToHand = axis == "Head to Hand";
            // Flash for a short time after a swing is fired
            bool justSwung = Time.time - PhysicalKnifeSwing.LastSwingTime < BlinkDuration;
            bool visible = justSwung ? Time.frameCount % 2 == 0 : true;

            if (ready && Inventory.main.GetHeldTool() is Knife knife)
            {
                Transform head = VRCameraRig.instance.vrCamera.transform;
                Transform hand = VRCameraRig.instance.laserPointer.transform;
                float length = Mathf.Max(knife.attackDist, SphereRadius * 2f);

                if (showVision)
                {
                    // Line of sight, from the head camera
                    PlaceCapsule(visionRenderer, head.position + head.forward * (length * 0.5f), head.forward, length);
                }
                if (showLaser)
                {
                    // Aim direction of the hand (laser pointer)
                    PlaceCapsule(laserRenderer, hand.position + hand.forward * (length * 0.5f), hand.forward, length);
                }
                if (showHeadToHand)
                {
                    // Direction from the head to the hand, where the knife is held
                    Vector3 direction = Vector3.Normalize(hand.position - head.position);
                    PlaceCapsule(headToHandRenderer, head.position + direction * (length * 0.5f), direction, length);
                }
            }

            visionRenderer.enabled = ready && showVision && visible;
            laserRenderer.enabled = ready && showLaser && visible;
            headToHandRenderer.enabled = ready && showHeadToHand && visible;
        }

        // The capsule's long axis is local Y, align it with the given direction
        private static void PlaceCapsule(Renderer renderer, Vector3 center, Vector3 direction, float length)
        {
            renderer.transform.position = center;
            renderer.transform.rotation = Quaternion.LookRotation(direction) * Quaternion.Euler(90f, 0f, 0f);
            renderer.transform.localScale = new Vector3(SphereRadius * 2f, length, SphereRadius * 2f);
        }
    }

    #region Patches

    // Attach KnifeHitboxDebug to the VRCameraRig when it's created
    [HarmonyPatch(typeof(VRCameraRig), nameof(VRCameraRig.SetupControllers))]
    static class AttachKnifeHitboxDebug
    {
        [HarmonyPostfix]
        static void Postfix(VRCameraRig __instance)
        {
            __instance.gameObject.GetOrAddComponent<KnifeHitboxDebug>();
        }
    }

    #endregion
}

using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Debug visual for the knife's melee hitbox: a capsule matching the game's trace
    /// (0.2 m sphere radius, knife attack distance) along the line of sight.
    /// Visible while a Knife/HeatBlade is held and the Debug Overlays option is on.
    /// </summary>
    public class KnifeHitboxDebug : MonoBehaviour
    {
        // Must match PlayerTool.TraceForTarget's default sphere radius
        private const float SphereRadius = 0.2f;

        // How long the hitbox flashes after a swing is fired
        private const float BlinkDuration = 0.2f;

        private static readonly Color HitboxColor = new Color(1f, 0.6f, 0f, 0.4f);

        private Renderer capsuleRenderer;

        void Start()
        {
            Material material = new Material(ShaderManager.preloadedShaders.DebugDisplaySolid);
            material.SetColor(ShaderPropertyID._Color, HitboxColor);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "KnifeHitboxDebug";
            go.transform.SetParent(transform, false);
            Destroy(go.GetComponent<CapsuleCollider>());
            capsuleRenderer = go.GetComponent<Renderer>();
            capsuleRenderer.material = material;
            capsuleRenderer.enabled = false;
        }

        void Update()
        {
            bool ready = Settings.IsDebugEnabled
                && VRCameraRig.instance != null && VRCameraRig.instance.vrCamera != null
                && Player.main != null
                && Inventory.main != null;

            if (ready && Inventory.main.GetHeldTool() is Knife knife)
            {
                // The hitbox follows the line of sight, from the head camera
                Transform head = VRCameraRig.instance.vrCamera.transform;
                float length = Mathf.Max(knife.attackDist, SphereRadius * 2f);

                // Keep the capsule GO active, toggle the renderer only.
                // Flash it for a short time after a swing is fired.
                bool justSwung = Time.time - PhysicalKnifeSwing.LastSwingTime < BlinkDuration;
                capsuleRenderer.enabled = justSwung ? Time.frameCount % 2 == 0 : true;
                // The capsule's long axis is local Y, align it with the line of sight
                capsuleRenderer.transform.position = head.position + head.forward * (length * 0.5f);
                capsuleRenderer.transform.rotation = Quaternion.LookRotation(head.forward) * Quaternion.Euler(90f, 0f, 0f);
                capsuleRenderer.transform.localScale = new Vector3(SphereRadius * 2f, length, SphereRadius * 2f);
            }
            else if (capsuleRenderer.enabled)
            {
                capsuleRenderer.enabled = false;
            }
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

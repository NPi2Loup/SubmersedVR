using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Debug visual for the knife's hitboxes: two wireframes flashing after a swing.
    /// Yellow = the swing detection probe (the zone that triggers the attack, follows
    /// the current probe settings). Orange = the game's real hitbox (0.2 m sphere
    /// radius swept for the attack distance along the aim) - where damage actually
    /// lands. Wireframe instead of a solid surface so it doesn't obscure the view.
    /// Visible while a Knife/HeatBlade is held and the Debug Overlays option is on.
    /// </summary>
    public class KnifeHitboxDebug : MonoBehaviour
    {
        // Must match PlayerTool.TraceForTarget's default sphere radius
        private const float SphereRadius = 0.2f;

        // How long the hitboxes flash after a swing is fired
        private const float BlinkDuration = 0.2f;

        private static readonly Color ProbeColor = new Color(1f, 1f, 0f, 0.9f);
        private static readonly Color GameHitboxColor = new Color(1f, 0.5f, 0f, 0.9f);

        private LineRenderer probeLine;
        private LineRenderer gameLine;

        void Start()
        {
            probeLine = CreateLine("KnifeSwingProbe", ProbeColor);
            gameLine = CreateLine("KnifeGameHitbox", GameHitboxColor);
        }

        private LineRenderer CreateLine(string name, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            Material material = new Material(ShaderManager.preloadedShaders.DebugDisplaySolid);
            material.SetColor(ShaderPropertyID._Color, color);
            line.material = material;
            line.startColor = color;
            line.endColor = color;
            line.startWidth = 0.01f;
            line.endWidth = 0.01f;
            line.useWorldSpace = true;
            line.positionCount = 0;
            line.enabled = false;
            return line;
        }

        void Update()
        {
            bool knifeHeld = Inventory.main != null && Inventory.main.GetHeldTool() is Knife;
            bool ready = Settings.IsDebugEnabled
                && VRCameraRig.instance != null && VRCameraRig.instance.rightController != null
                && VRCameraRig.instance.laserPointer != null
                && Player.main != null
                && knifeHeld;

            // Flash for a short time after a swing is fired
            bool justSwung = Time.time - PhysicalKnifeSwing.LastSwingTime < BlinkDuration;
            bool visible = justSwung ? Time.frameCount % 2 == 0 : true;

            if (ready && Inventory.main.GetHeldTool() is Knife knife)
            {
                Transform hand = VRCameraRig.instance.rightController.transform;
                Vector3 aim = VRCameraRig.instance.laserPointer.transform.forward;
                float length = Mathf.Max(knife.attackDist, SphereRadius * 2f);
                float scale = Settings.KnifeProbeRadiusScale;

                // A: the current probe (detection zone that triggers the attack)
                var probePts = new List<Vector3>(256);
                BuildCapsuleWireframe(probePts, hand.position + aim * (length * 0.5f), aim, length, SphereRadius * scale);
                SetLine(probeLine, probePts);

                // B: the game's real hitbox (always the unscaled game geometry)
                var gamePts = new List<Vector3>(256);
                BuildCapsuleWireframe(gamePts, hand.position + aim * (length * 0.5f), aim, length, SphereRadius);
                SetLine(gameLine, gamePts);
            }

            probeLine.enabled = ready && visible;
            gameLine.enabled = ready && visible;
        }

        private static void SetLine(LineRenderer line, List<Vector3> points)
        {
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                line.SetPosition(i, points[i]);
            }
        }

        // Wireframe of a capsule (cylinder + 2 hemispheres) matching the game's trace:
        // meridians around the axis plus a few cross-section rings.
        private static void BuildCapsuleWireframe(List<Vector3> points, Vector3 center, Vector3 direction, float length, float radius)
        {
            Vector3 dir = direction.normalized;
            GetBasis(dir, out Vector3 u, out Vector3 v);
            float a = length * 0.5f;
            float c = Mathf.Max(a - radius, 0f);

            const int Meridians = 8;
            const int HemSamples = 6;
            const int CylSamples = 4;

            var stroke = new List<Vector3>(HemSamples * 2 + CylSamples + 2);
            for (int m = 0; m < Meridians; m++)
            {
                float theta = m * Mathf.PI * 2f / Meridians;
                Vector3 radialDir = u * Mathf.Cos(theta) + v * Mathf.Sin(theta);
                stroke.Clear();
                // Bottom pole
                stroke.Add(center - dir * a);
                // Bottom hemisphere
                for (int i = 1; i <= HemSamples; i++)
                {
                    float phi = i * Mathf.PI * 0.5f / HemSamples;
                    stroke.Add(center + dir * (-c - radius * Mathf.Cos(phi)) + radialDir * (radius * Mathf.Sin(phi)));
                }
                // Cylinder
                for (int i = 1; i <= CylSamples; i++)
                {
                    stroke.Add(center + dir * (-c + 2f * c * i / CylSamples) + radialDir * radius);
                }
                // Top hemisphere
                for (int i = 1; i <= HemSamples; i++)
                {
                    float phi = i * Mathf.PI * 0.5f / HemSamples;
                    stroke.Add(center + dir * (c + radius * Mathf.Cos(phi)) + radialDir * (radius * Mathf.Sin(phi)));
                }
                // Top pole
                stroke.Add(center + dir * a);
                AppendStroke(points, stroke);
            }

            // Cross-section rings
            const int RingSegments = 16;
            foreach (float axial in new[] { -c, 0f, c })
            {
                stroke.Clear();
                for (int i = 0; i <= RingSegments; i++)
                {
                    float theta = i * Mathf.PI * 2f / RingSegments;
                    Vector3 radialDir = u * Mathf.Cos(theta) + v * Mathf.Sin(theta);
                    stroke.Add(center + dir * axial + radialDir * radius);
                }
                AppendStroke(points, stroke);
            }
        }

        private static void GetBasis(Vector3 dir, out Vector3 u, out Vector3 v)
        {
            Vector3 refVec = Mathf.Abs(dir.y) > 0.99f ? Vector3.right : Vector3.up;
            u = Vector3.Cross(dir, refVec).normalized;
            v = Vector3.Cross(dir, u);
        }

        // LineRenderer connects consecutive points, so duplicate the last point to break between strokes
        private static void AppendStroke(List<Vector3> points, List<Vector3> stroke)
        {
            if (points.Count > 0)
            {
                points.Add(points[points.Count - 1]);
            }
            points.AddRange(stroke);
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

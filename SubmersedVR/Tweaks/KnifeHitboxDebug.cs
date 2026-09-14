using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Debug visual for the knife's melee hitbox: a wireframe matching the game's trace
    /// (0.2 m sphere radius, knife attack distance) along a selectable axis
    /// (line of sight, laser pointer aim, head to hand, or the last swing direction).
    /// Wireframe instead of a solid surface so it doesn't obscure the view.
    /// Visible while a Knife/HeatBlade is held and the Debug Overlays option is on.
    /// </summary>
    public class KnifeHitboxDebug : MonoBehaviour
    {
        // Must match PlayerTool.TraceForTarget's default sphere radius
        private const float SphereRadius = 0.2f;

        // How long the hitbox flashes after a swing is fired
        private const float BlinkDuration = 0.2f;

        private static readonly Color VisionColor = new Color(1f, 0.6f, 0f, 0.9f);
        private static readonly Color LaserColor = new Color(0f, 1f, 1f, 0.9f);
        private static readonly Color HeadToHandColor = new Color(1f, 1f, 0f, 0.9f);
        private static readonly Color SwingColor = new Color(1f, 0f, 1f, 0.9f);

        private LineRenderer visionLine;
        private LineRenderer laserLine;
        private LineRenderer headToHandLine;
        private LineRenderer swingLine;

        void Start()
        {
            visionLine = CreateLine("KnifeHitboxVision", VisionColor);
            laserLine = CreateLine("KnifeHitboxLaser", LaserColor);
            headToHandLine = CreateLine("KnifeHitboxHeadToHand", HeadToHandColor);
            swingLine = CreateLine("KnifeHitboxSwing", SwingColor);
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
                && VRCameraRig.instance != null && VRCameraRig.instance.vrCamera != null
                && VRCameraRig.instance.laserPointer != null
                && Player.main != null
                && knifeHeld;

            string axis = Settings.KnifeHitboxAxis;
            bool showVision = axis == "Vision" || axis == "Vision + Laser";
            bool showLaser = axis == "Laser Pointer" || axis == "Vision + Laser";
            bool showHeadToHand = axis == "Head to Hand";
            bool showSwing = axis == "Swing Direction";
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
                    var pts = new List<Vector3>(256);
                    BuildCapsuleWireframe(pts, head.position + head.forward * (length * 0.5f), head.forward, length);
                    SetLine(visionLine, pts);
                }
                if (showLaser)
                {
                    // Aim direction of the hand (laser pointer)
                    var pts = new List<Vector3>(256);
                    BuildCapsuleWireframe(pts, hand.position + hand.forward * (length * 0.5f), hand.forward, length);
                    SetLine(laserLine, pts);
                }
                if (showHeadToHand)
                {
                    // Direction from the head to the hand, where the knife is held
                    Vector3 direction = Vector3.Normalize(hand.position - head.position);
                    var pts = new List<Vector3>(256);
                    BuildCapsuleWireframe(pts, head.position + direction * (length * 0.5f), direction, length);
                    SetLine(headToHandLine, pts);
                }
                if (showSwing)
                {
                    // Last swing direction: box with length along the movement,
                    // width along the pointer, fixed height
                    Vector3 longAxis = PhysicalKnifeSwing.LastSwingDirection;
                    Vector3 wide = hand.forward - longAxis * Vector3.Dot(hand.forward, longAxis);
                    if (wide.sqrMagnitude < 0.0001f)
                    {
                        wide = Vector3.Cross(longAxis, Vector3.up);
                    }
                    wide.Normalize();
                    Vector3 highAxis = Vector3.Cross(longAxis, wide).normalized;
                    var pts = new List<Vector3>(64);
                    BuildBoxWireframe(pts, hand.position + longAxis * (length * 0.5f), longAxis, wide, highAxis, length, SphereRadius * 2f, SphereRadius * 2f);
                    SetLine(swingLine, pts);
                }
            }

            visionLine.enabled = ready && showVision && visible;
            laserLine.enabled = ready && showLaser && visible;
            headToHandLine.enabled = ready && showHeadToHand && visible;
            swingLine.enabled = ready && showSwing && visible;
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
        private static void BuildCapsuleWireframe(List<Vector3> points, Vector3 center, Vector3 direction, float length)
        {
            Vector3 dir = direction.normalized;
            GetBasis(dir, out Vector3 u, out Vector3 v);
            float a = length * 0.5f;
            float c = Mathf.Max(a - SphereRadius, 0f);

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
                    stroke.Add(center + dir * (-c - SphereRadius * Mathf.Cos(phi)) + radialDir * (SphereRadius * Mathf.Sin(phi)));
                }
                // Cylinder
                for (int i = 1; i <= CylSamples; i++)
                {
                    stroke.Add(center + dir * (-c + 2f * c * i / CylSamples) + radialDir * SphereRadius);
                }
                // Top hemisphere
                for (int i = 1; i <= HemSamples; i++)
                {
                    float phi = i * Mathf.PI * 0.5f / HemSamples;
                    stroke.Add(center + dir * (c + SphereRadius * Mathf.Cos(phi)) + radialDir * (SphereRadius * Mathf.Sin(phi)));
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
                    stroke.Add(center + dir * axial + radialDir * SphereRadius);
                }
                AppendStroke(points, stroke);
            }
        }

        // Wireframe of a box (12 edges)
        private static void BuildBoxWireframe(List<Vector3> points, Vector3 center, Vector3 longAxis, Vector3 wideAxis, Vector3 highAxis, float length, float width, float height)
        {
            Vector3 hl = longAxis * (length * 0.5f);
            Vector3 hw = wideAxis * (width * 0.5f);
            Vector3 hh = highAxis * (height * 0.5f);
            Vector3[] corners =
            {
                center - hl - hw - hh,
                center + hl - hw - hh,
                center + hl + hw - hh,
                center - hl + hw - hh,
                center - hl - hw + hh,
                center + hl - hw + hh,
                center + hl + hw + hh,
                center - hl + hw + hh,
            };
            int[] edgeA = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
            int[] edgeB = { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };
            var stroke = new List<Vector3>(2);
            for (int i = 0; i < edgeA.Length; i++)
            {
                stroke.Clear();
                stroke.Add(corners[edgeA[i]]);
                stroke.Add(corners[edgeB[i]]);
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

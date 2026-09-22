using System.Collections.Generic;
using UnityEngine;

namespace SubmersedVR
{
    // Splits the hand reticle for laser pointer mode: the target action
    // (compTextHand/HandSubscript, e.g. "Climb the ladder [A]") moves to a
    // second world canvas that ReticleBillboard projects on the laser hit
    // point, while the tool info (use texts, energy, progress, icons) stays
    // on the root canvas at the hand. Split only registers the parts; all
    // movement is driven per-frame by Enforce (see ReticleBillboard), which
    // also honors the ReticlePointerSplit setting (off = original layout).
    static class ReticleSplit
    {
        class MovedPart
        {
            public RectTransform rt;
            public Transform originalParent;
            public Quaternion originalLocalRot;
            public Vector3 originalLocalScale;
            public Vector2 originalAnchorMin;
            public Vector2 originalAnchorMax;
            public Vector2 originalPivot;
            public Vector2 originalAnchoredPos;
            // Layout Y on the target canvas, re-applied by Enforce
            public float splitY;
        }

        // A part re-parented out of the icon canvas so it does not follow
        // the icon to the hit point (progress donut when the setting is off)
        class KeptPart
        {
            public RectTransform rt;
            public Transform originalParent;
            public Vector2 originalAnchorMin;
            public Vector2 originalAnchorMax;
            public Vector2 originalPivot;
            public Vector2 originalAnchoredPos;
        }

        static GameObject targetCanvasGo;
        static List<MovedPart> moved = new List<MovedPart>();
        static List<KeptPart> kept = new List<KeptPart>();
        // Reticle root currently split (null when unsplit); the VRCameraRig
        // watchdog re-applies the setup when a new reticle instance appears
        // (save/level load recreates it after VRHud.Setup already ran)
        static Transform splitRoot;

        public static Transform TargetCanvas => targetCanvasGo != null ? targetCanvasGo.transform : null;

        public static Transform SplitRoot => splitRoot;

        public static void Split(Camera uiCamera)
        {
            Unsplit();
            reparentLogCount = 0;
            if (HandReticle.main == null)
            {
                return;
            }
            // On save/level load the reticle is recreated empty (texts and
            // RectTransform sizes are zero): the game lays it out a few
            // frames later. Splitting the zero state freezes a broken
            // layout, so wait until the game has laid it out once (the
            // VRCameraRig watchdog retries every frame)
            var handText = HandReticle.main.compTextHand;
            var handRt = handText != null ? handText.transform as RectTransform : null;
            if (handRt != null && handRt.sizeDelta.x <= 0.5f && handRt.rect.width <= 0.5f)
            {
                return;
            }
            var root = HandReticle.main.gameObject.transform;
            splitRoot = root;
            targetCanvasGo = new GameObject("TargetReticleCanvas", typeof(RectTransform));
            targetCanvasGo.transform.SetParent(root, false);

            var canvas = targetCanvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = uiCamera;
            var rootCanvas = root.GetComponent<Canvas>();
            if (rootCanvas != null)
            {
                canvas.sortingOrder = rootCanvas.sortingOrder;
                canvas.sortingLayerID = rootCanvas.sortingLayerID;
            }
            targetCanvasGo.layer = root.gameObject.layer;

            // No uGUI_CanvasScaler clone: the billboard drives the canvas
            // transform, and a runtime clone would miss the prefab _anchor.
            // Log the root's scaler (if any) to confirm in game
            var rootScaler = root.GetComponent<uGUI_CanvasScaler>();
            if (rootScaler != null && Settings.IsDebugEnabled)
            {
                Mod.logger.LogInfo($"[ReticleSplit] Root uGUI_CanvasScaler: refRes {rootScaler.referenceResolution} mode {rootScaler.mode} vrMode {rootScaler.vrMode}");
            }

            // The primary action on the focused target (e.g. "Climb the
            // ladder [A]") is the target info: it goes to the hit point,
            // with its action icon and the repair progress donut (%). The
            // tool info (use texts + energy) stays on the hand canvas.
            // Like the original hand layout: icon on top, text below
            var iconRt = HandReticle.main.iconCanvas;
            RegisterPart(iconRt, 26f);
            // The progress donut and % label: a child of the icon container
            // follows the icon move, a sibling has to be registered on its
            // own. When the setting is off they stay on the tool: children
            // of the icon are re-parented out, siblings are left alone
            if (Settings.ReticleProgressAtPointer)
            {
                foreach (var comp in new Component[] { HandReticle.main.progressImage, HandReticle.main.progressText })
                {
                    if (comp == null)
                    {
                        continue;
                    }
                    if (!comp.transform.IsChildOf(iconRt))
                    {
                        RegisterPart(comp, 26f);
                    }
                }
            }
            else
            {
                KeepProgressOnTool(iconRt);
            }
            RegisterPart(HandReticle.main.compTextHand, -34f);
            RegisterPart(HandReticle.main.compTextHandSubscript, -56f);
        }

        // Captures the original layout for RestorePart and the split layout
        // for Enforce, without moving anything: Enforce places the parts
        static void RegisterPart(Component comp, float y)
        {
            if (comp == null)
            {
                return;
            }
            var rt = (RectTransform)comp.transform;
            var part = new MovedPart
            {
                rt = rt,
                originalParent = rt.parent,
                originalLocalRot = rt.localRotation,
                originalLocalScale = rt.localScale,
                originalAnchorMin = rt.anchorMin,
                originalAnchorMax = rt.anchorMax,
                originalPivot = rt.pivot,
                originalAnchoredPos = rt.anchoredPosition,
                splitY = y,
            };
            moved.Add(part);
            if (Settings.IsDebugEnabled)
            {
                Mod.logger.LogInfo($"[ReticleSplit] registered {rt.name}: anchors {rt.anchorMin}/{rt.anchorMax} pivot {rt.pivot} size {rt.sizeDelta} pos {rt.anchoredPosition}");
            }
        }

        // Re-parents the progress container (a child of the icon canvas) onto
        // the root canvas, keeping its on-screen position, so the donut stays
        // on the tool instead of following the icon to the hit point
        static void KeepProgressOnTool(Transform iconRt)
        {
            if (iconRt == null || iconRt.parent == null)
            {
                return;
            }
            foreach (var comp in new Component[] { HandReticle.main.progressImage, HandReticle.main.progressText })
            {
                if (comp == null)
                {
                    continue;
                }
                // Climb to the icon canvas direct child (the "Progress" container)
                var t = comp.transform;
                while (t != null && t.parent != iconRt)
                {
                    t = t.parent;
                }
                if (t == null)
                {
                    continue; // sibling of the icon, not re-parented by the split
                }
                var rt = (RectTransform)t;
                kept.Add(new KeptPart
                {
                    rt = rt,
                    originalParent = iconRt,
                    originalAnchorMin = rt.anchorMin,
                    originalAnchorMax = rt.anchorMax,
                    originalPivot = rt.pivot,
                    originalAnchoredPos = rt.anchoredPosition,
                });
                rt.SetParent(iconRt.parent, true);
            }
        }

        // The game re-parents the primary action text to its icon every frame
        // (icon-driven layout), so the split must be re-asserted per frame.
        // Called from ReticleBillboard.LateUpdate with aiming = world target
        // AND the ReticlePointerSplit setting: true puts the parts on the
        // target canvas with the split layout, false puts them back on the
        // hand with the original layout (the setting off = original behavior)
        static int reparentLogCount;

        public static void Enforce(bool aiming)
        {
            if (targetCanvasGo == null)
            {
                return;
            }
            var target = targetCanvasGo.transform;
            foreach (var part in moved)
            {
                if (part.rt == null)
                {
                    continue;
                }
                if (aiming)
                {
                    if (part.rt.parent == target)
                    {
                        continue;
                    }
                    if (Settings.IsDebugEnabled && reparentLogCount < 10)
                    {
                        reparentLogCount++;
                        var gameParent = part.rt.parent != null ? part.rt.parent.name : "null";
                        Mod.logger.LogInfo($"[ReticleSplit] Enforce: {part.rt.name} re-parented by game to {gameParent}, moving back to target");
                    }
                    part.rt.SetParent(target, false);
                    part.rt.anchorMin = part.rt.anchorMax = new Vector2(0.5f, 0.5f);
                    part.rt.pivot = new Vector2(0.5f, 0.5f);
                    part.rt.anchoredPosition = new Vector2(0f, part.splitY);
                }
                else if (part.rt.parent == target)
                {
                    // Lost the target: back to the hand, original layout
                    RestorePart(part);
                }
            }
        }

        // Puts a part back on its original parent with the original layout;
        // the game re-lays it out (icon-driven) on the following frames
        static void RestorePart(MovedPart part)
        {
            if (part.rt == null || part.originalParent == null)
            {
                return;
            }
            part.rt.SetParent(part.originalParent, false);
            // Anchors first: the anchored position only maps to the local
            // position once the original anchors are back
            part.rt.anchorMin = part.originalAnchorMin;
            part.rt.anchorMax = part.originalAnchorMax;
            part.rt.pivot = part.originalPivot;
            part.rt.anchoredPosition = part.originalAnchoredPos;
            part.rt.localRotation = part.originalLocalRot;
            part.rt.localScale = part.originalLocalScale;
        }

        public static void Unsplit()
        {
            foreach (var part in moved)
            {
                // Destroyed objects (level load without unsplit) are skipped
                RestorePart(part);
            }
            moved.Clear();
            // Back to the icon canvas once the icon itself is restored
            foreach (var part in kept)
            {
                if (part.rt != null && part.originalParent != null)
                {
                    part.rt.SetParent(part.originalParent, false);
                    part.rt.anchorMin = part.originalAnchorMin;
                    part.rt.anchorMax = part.originalAnchorMax;
                    part.rt.pivot = part.originalPivot;
                    part.rt.anchoredPosition = part.originalAnchoredPos;
                }
            }
            kept.Clear();
            splitRoot = null;
            if (targetCanvasGo != null)
            {
                UnityEngine.Object.Destroy(targetCanvasGo);
                targetCanvasGo = null;
            }
        }
    }
}

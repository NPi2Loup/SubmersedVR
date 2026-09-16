using System.Collections.Generic;
using UnityEngine;

namespace SubmersedVR
{
    // Splits the hand reticle for laser pointer mode: the target action
    // (compTextHand/HandSubscript, e.g. "Climb the ladder [A]") moves to a
    // second world canvas that ReticleBillboard projects on the laser hit
    // point, while the tool info (use texts, energy, progress, icons) stays
    // on the root canvas at the hand.
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

        static GameObject targetCanvasGo;
        static List<MovedPart> moved = new List<MovedPart>();
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
            MovePart(iconRt, 26f);
            // The donut and % label: a child of the icon container follows
            // the icon move, a sibling has to be moved on its own
            foreach (var comp in new Component[] { HandReticle.main.progressImage, HandReticle.main.progressText })
            {
                if (comp == null)
                {
                    continue;
                }
                if (!comp.transform.IsChildOf(iconRt))
                {
                    MovePart(comp, 26f);
                }
            }
            MovePart(HandReticle.main.compTextHand, -34f);
            MovePart(HandReticle.main.compTextHandSubscript, -56f);
        }

        static void MovePart(Component comp, float y)
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
            };
            rt.SetParent(targetCanvasGo.transform, false);
            part.splitY = y;
            moved.Add(part);
            if (Settings.IsDebugEnabled)
            {
                Mod.logger.LogInfo($"[ReticleSplit] {rt.name}: anchors {rt.anchorMin}/{rt.anchorMax} pivot {rt.pivot} size {rt.sizeDelta} pos {rt.anchoredPosition}");
            }
            // Initial layout on the target canvas (to be tuned later);
            // sizeDelta stays as is
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, y);
        }

        // The game re-parents the primary action text to its icon every frame
        // (icon-driven layout), undoing the one-shot Split. Called from
        // ReticleBillboard.LateUpdate: while aiming, a part moved back by the
        // game goes to the target canvas with the split layout; without a
        // world target (e.g. build mode) the parts live on the hand with the
        // original layout, so the action info stays visible
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
            splitRoot = null;
            if (targetCanvasGo != null)
            {
                UnityEngine.Object.Destroy(targetCanvasGo);
                targetCanvasGo = null;
            }
        }
    }
}

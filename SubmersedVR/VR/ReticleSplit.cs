using System.Collections.Generic;
using UnityEngine;

namespace SubmersedVR
{
    // Splits the hand reticle for laser pointer mode: the tool info (hand
    // texts + icons) stays on the root canvas at the hand, while the target
    // info (use texts + progress) is moved to a second world canvas that
    // ReticleBillboard projects on the laser hit point.
    static class ReticleSplit
    {
        class MovedPart
        {
            public RectTransform rt;
            public Transform originalParent;
            public Vector3 originalLocalPos;
            public Quaternion originalLocalRot;
            public Vector3 originalLocalScale;
            public Vector2 originalAnchorMin;
            public Vector2 originalAnchorMax;
            public Vector2 originalPivot;
        }

        static GameObject targetCanvasGo;
        static List<MovedPart> moved = new List<MovedPart>();

        public static Transform TargetCanvas => targetCanvasGo != null ? targetCanvasGo.transform : null;

        public static void Split(Camera uiCamera)
        {
            Unsplit();
            if (HandReticle.main == null)
            {
                return;
            }
            var root = HandReticle.main.gameObject.transform;
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

            var rootScaler = root.GetComponent<uGUI_CanvasScaler>();
            if (rootScaler != null)
            {
                var scaler = targetCanvasGo.AddComponent<uGUI_CanvasScaler>();
                scaler.referenceResolution = rootScaler.referenceResolution;
                scaler.mode = rootScaler.mode;
                scaler.vrMode = rootScaler.vrMode;
                scaler.distance = rootScaler.distance;
                scaler.scaleMode = rootScaler.scaleMode;
                scaler.SetDirty();
                if (Settings.IsDebugEnabled)
                {
                    Mod.logger.LogInfo($"[ReticleSplit] Root uGUI_CanvasScaler: refRes {rootScaler.referenceResolution} mode {rootScaler.mode} vrMode {rootScaler.vrMode}");
                }
            }

            MovePart(HandReticle.main.compTextUse, 0f);
            MovePart(HandReticle.main.compTextUseSubscript, -40f);
            MovePart(HandReticle.main.progressImage, -80f);
            MovePart(HandReticle.main.progressText, -100f);
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
                originalLocalPos = rt.localPosition,
                originalLocalRot = rt.localRotation,
                originalLocalScale = rt.localScale,
                originalAnchorMin = rt.anchorMin,
                originalAnchorMax = rt.anchorMax,
                originalPivot = rt.pivot,
            };
            rt.SetParent(targetCanvasGo.transform, false);
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

        public static void Unsplit()
        {
            foreach (var part in moved)
            {
                part.rt.SetParent(part.originalParent, false);
                part.rt.localPosition = part.originalLocalPos;
                part.rt.localRotation = part.originalLocalRot;
                part.rt.localScale = part.originalLocalScale;
                part.rt.anchorMin = part.originalAnchorMin;
                part.rt.anchorMax = part.originalAnchorMax;
                part.rt.pivot = part.originalPivot;
            }
            moved.Clear();
            if (targetCanvasGo != null)
            {
                UnityEngine.Object.Destroy(targetCanvasGo);
                targetCanvasGo = null;
            }
        }
    }
}

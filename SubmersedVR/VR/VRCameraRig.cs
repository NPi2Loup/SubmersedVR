using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using System.Collections;
using UWE;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.EventSystems;
using System.IO;

/*
The VRCamera Rig handles the controllers together with their laser pointers to control the UI.
TODO: This class and file does too much at the moment. Need to refactor stuff out and clean things up a bit more.
*/
namespace SubmersedVR
{
    extern alias SteamVRActions;
    extern alias SteamVRRef;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;

    class VRCameraRig : MonoBehaviour
    {
        // Setup and created in Start()
        public Camera vrCamera;
        public GameObject leftController;
        public GameObject rightController;
        // Those are used for the IK/Hands
        public GameObject leftHandTarget;
        public GameObject rightHandTarget;

        // TODO: Those should not be full laserpointers probably
        // The only thing I need is cameras at different positions for the UI or Worldspace Raycasts
        public LaserPointer laserPointer;
        public LaserPointer laserPointerLeft;
        public GameObject movementLaser;
        public GameObject movementLaserLeft;

        public GameObject uiRig;
        public GameObject leftControllerUI;
        public GameObject rightControllerUI;
        public LaserPointer laserPointerUI;

        public GameObject modelL;
        public GameObject modelR;

        public static VRCameraRig instance;

        public Camera uiCamera = null;
        public GameObject worldTarget;
        public float worldTargetDistance;
        public float worldTargetTime;
        public Transform rigParentTarget;
        float smoothedRigY = float.NaN;
        bool hasLastTargetPos;
        Vector3 lastTargetPos;
        int lastBobLogFrame = -1;
        int bobBurstFrames;
        int bobBurstCooldown;
        float lastRawY;
        bool hasLastRawY;
        bool wasInVehicle;
        readonly NotchFilter vehicleWaveNotch = new NotchFilter();
        // On foot: the smoothing lag is VerticalSmoothing * MaxSmoothingLag
        const float MaxSmoothingLag = 0.6f;

        // 2nd-order notch (biquad band-reject, RBJ form). The coefficients
        // are re-derived each frame from the current dt (the VR render rate
        // varies): the band around F0 is removed while DC/slow signals pass
        // with ~zero lag - in a vehicle only the wave heave band is damped,
        // the dive/terrain-follow trend stays glued
        class NotchFilter
        {
            const float F0 = 0.4f;
            const float Q = 1.8f;
            float x1, x2, y1, y2;

            public void Reset()
            {
                x1 = 0f;
                x2 = 0f;
                y1 = 0f;
                y2 = 0f;
            }

            // Seed the state so the first frame has no transient
            public void Seed(float x)
            {
                x1 = x;
                x2 = x;
                y1 = x;
                y2 = x;
            }

            public float Process(float x, float dt)
            {
                if (dt <= 0f)
                {
                    return x;
                }
                float w0 = 2f * Mathf.PI * F0 * dt;
                float c = Mathf.Cos(w0);
                float a = Mathf.Sin(w0) / (2f * Q);
                float y = (x - 2f * c * x1 + x2 + 2f * c * y1 - (1f - a) * y2) / (1f + a);
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                return y;
            }
        }

        public Camera UIControllerCamera
        {
            get
            {
                if (laserPointerUI == null)
                {
                    return null;
                }
                return laserPointerUI.eventCamera;
            }
        }
        public Camera WorldControllerCamera
        {
            get
            {
                if (laserPointer == null)
                {
                    return null;
                }
                return laserPointer.eventCamera;
            }
        }

        private FPSInputModule fpsInput = null;

        // This transfroms forward vector determines where the equiped tool will be aiming
        public static readonly TransformOffset DefaultTargetTransform = new TransformOffset(Vector3.zero, new Vector3(45, 0, 0));
        private TransformOffset _targetTransform;
        public VRQuickSlots VrQuickSlots;

        public TransformOffset TargetTransform
        {
            get
            {
                return _targetTransform;
            }
            set
            {
                _targetTransform = value;
                value.Apply(laserPointerUI.transform);
                value.Apply(laserPointer.transform);
                value.Apply(laserPointerLeft.transform);
            }
        }

        public static Transform GetTargetTansform()
        {
            return VRCameraRig.instance.laserPointer.transform;
        }
        public static Transform GetLeftTargetTansform()
        {
            return VRCameraRig.instance.laserPointerLeft.transform;
        }

        public void SetCameraTrackTarget(Transform target)
        {
            this.rigParentTarget = target;
        }

        public void SetupControllers()
        {
            // TODO: Naming is inconsistent, clean this mess up, only need 1/2 pointers?
            leftController = new GameObject(nameof(leftController)).WithParent(transform);
            rightController = new GameObject(nameof(rightController)).WithParent(transform);

            leftController.SetActive(false);
            rightController.SetActive(false);
            var controller = leftController.AddComponent<SteamVRRef.Valve.VR.SteamVR_Behaviour_Pose>();
            controller.inputSource = SteamVRRef.Valve.VR.SteamVR_Input_Sources.LeftHand;
            controller.poseAction = SteamVRActions.Valve.VR.SteamVR_Actions.subnautica_LeftHandPose;
            controller = rightController.AddComponent<SteamVRRef.Valve.VR.SteamVR_Behaviour_Pose>();
            controller.inputSource = SteamVRRef.Valve.VR.SteamVR_Input_Sources.RightHand;
            controller.poseAction = SteamVRActions.Valve.VR.SteamVR_Actions.subnautica_RightHandPose;
            leftController.SetActive(true);
            rightController.SetActive(true);

            leftHandTarget = new GameObject(nameof(leftHandTarget)).WithParent(leftController);
            rightHandTarget = new GameObject(nameof(rightHandTarget)).WithParent(rightController);
            leftHandTarget.transform.localEulerAngles = new Vector3(270, 90, 0);
            Vector3 handOffset = new Vector3(90, 270, 0);
            rightHandTarget.transform.localEulerAngles = handOffset;

            // Laser Pointer Setup
            laserPointer = new GameObject(nameof(laserPointer)).WithParent(rightController.transform).AddComponent<LaserPointer>();
            laserPointerLeft = new GameObject(nameof(laserPointerLeft)).WithParent(leftController.transform).AddComponent<LaserPointer>();
            laserPointerLeft.gameObject.SetActive(false);
            // laserPointer.gameObject.SetActive(false);
            laserPointer.disableAfterCreation = true;

            // Movement laser setup (visual for the hand-based movement axis, see MovementLaser)
            var rightLaser = new GameObject(nameof(movementLaser)).WithParent(rightController.transform).AddComponent<MovementLaser>();
            rightLaser.isLeftHand = false;
            movementLaser = rightLaser.gameObject;
            var leftLaser = new GameObject(nameof(movementLaserLeft)).WithParent(leftController.transform).AddComponent<MovementLaser>();
            leftLaser.isLeftHand = true;
            movementLaserLeft = leftLaser.gameObject;

            // NOTE: These laserpointer and controllers is NOT parented to the Rig, since they act in UI space, not world space
            uiRig = new GameObject(nameof(uiRig));
            Object.DontDestroyOnLoad(uiRig);
            leftControllerUI = new GameObject(nameof(leftControllerUI)).WithParent(uiRig.transform);
            rightControllerUI = new GameObject(nameof(rightControllerUI)).WithParent(uiRig.transform);
            laserPointerUI = new GameObject(nameof(laserPointerUI)).WithParent(rightControllerUI.transform).AddComponent<LaserPointer>();
            // TODO: Constructors possible?
            laserPointerUI.doWorldRaycasts = true;
            laserPointerUI.useUILayer = true;

            leftControllerUI.SetActive(false);
            rightControllerUI.SetActive(false);
            controller = leftControllerUI.AddComponent<SteamVRRef.Valve.VR.SteamVR_Behaviour_Pose>();
            controller.inputSource = SteamVRRef.Valve.VR.SteamVR_Input_Sources.LeftHand;
            controller.poseAction = SteamVRActions.Valve.VR.SteamVR_Actions.subnautica_LeftHandPose;
            controller = rightControllerUI.AddComponent<SteamVRRef.Valve.VR.SteamVR_Behaviour_Pose>();
            controller.inputSource = SteamVRRef.Valve.VR.SteamVR_Input_Sources.RightHand;
            controller.poseAction = SteamVRActions.Valve.VR.SteamVR_Actions.subnautica_RightHandPose;
            leftControllerUI.SetActive(true);
            rightControllerUI.SetActive(true);
            TargetTransform = DefaultTargetTransform;

            SetupControllerModels();

            // Connect Input module and layer pointer together
            // TODO: This should be easier using singleton setup
            fpsInput = FindObjectOfType<FPSInputModule>();
            laserPointer.inputModule = fpsInput;
            laserPointerLeft.inputModule = fpsInput;
            laserPointerUI.inputModule = fpsInput;
        }

        public void Awake()
        {
            SteamVR.Initialize();
            SteamVR.settings.trackingSpace = ETrackingUniverseOrigin.TrackingUniverseSeated;
            SteamVrGameInput.IsSteamVrReady = SteamVR.initializedState == SteamVR.InitializedStates.InitializeSuccess;
        }

        public void Start()
        {
            Settings.AmbientOcclusionSettingsChanged -= OnAmbientOcclusionSettingsChanged;
            Settings.AmbientOcclusionSettingsChanged += OnAmbientOcclusionSettingsChanged;

            SetupControllers();
            StartCoroutine(DelayedRecenter(1.0f));
        }

        public IEnumerator DelayedRecenter(float delay)
        {
            yield return new WaitForSeconds(delay);
            VRUtil.Recenter();
        }

        private void SetupControllerModels()
        {
            modelL = new GameObject(nameof(modelL)).WithParent(leftControllerUI).ResetTransform();
            modelR = new GameObject(nameof(modelR)).WithParent(rightControllerUI).ResetTransform();

            var model = modelR.AddComponent<SteamVRRef.Valve.VR.SteamVR_RenderModel>();
            model.SetInputSource(SteamVRRef.Valve.VR.SteamVR_Input_Sources.RightHand);
            model = modelL.AddComponent<SteamVRRef.Valve.VR.SteamVR_RenderModel>();
            model.SetInputSource(SteamVRRef.Valve.VR.SteamVR_Input_Sources.LeftHand);
            modelL.layer = LayerID.UI;
            modelR.layer = LayerID.UI;

            Settings.AlwaysShowControllersChanged += (_) => { UpdateShowControllers(); };
        }

        public void UpdateShowControllers()
        {
            var inMainMenu = !uGUI.isMainLevel;
            bool alwaysShow = Settings.AlwaysShowControllers;
            modelL?.SetActive(alwaysShow || inMainMenu);
            modelR?.SetActive(alwaysShow || inMainMenu);
        }

        public static void OnAmbientOcclusionSettingsChanged()
        {
            AmbientOcclusionVR.OnAmbientOcclusionSettingsChanged(VRCameraRig.instance.vrCamera);
        }

        // This is used to get the camera from the main menu
        // Main issue with making a new camera was the water surface but that should also be fixable
        // TODO: Maybe remove this, so we only have one common camera
        public void StealCamera(Camera camera)
        {
            // Destroy/Delete old camera
            // NOTE: Subnautica renderes the water using specific camera component which also renders when the camera is disabled

            if (camera != vrCamera && vrCamera != null)
            {
                vrCamera.enabled = false;
                Destroy(vrCamera.gameObject);
            }

            vrCamera = camera;
            Vector3 oldPos = camera.transform.position;
            transform.position = oldPos;
            vrCamera.transform.parent = this.transform;
            // Re-seed the vertical smoothing for the new camera/rig position
            smoothedRigY = float.NaN;
            vehicleWaveNotch.Reset();

            AmbientOcclusionVR.AddOcclusionEffect(vrCamera);
        }

        public void StealUICamera(Camera camera, bool fromGame = false)
        {
            uiRig.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
            if (uiCamera != null)
            {
                uiCamera.transform.DetachChildren();
                Destroy(uiCamera.gameObject);
            }

            if (fromGame)
            {
                // This fixes a weird issue I had, where the UI Camera from the game would behave like it wasnt moving
                // even though the transform was changing properly.
                // Maybe it is because the tracking was once disabled in the main game, but I am not sure, since I tried enabling it too.
                // Copying the properties from the main camera and setting up the original important properties fixed it.
                uiRig.transform.position = Vector3.zero;
                var oldMask = camera.cullingMask;
                var oldClear = camera.clearFlags;
                var oldDepth = camera.depth;

                camera.CopyFrom(SNCameraRoot.main.mainCamera);
                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;
                camera.renderingPath = RenderingPath.Forward;
                camera.cullingMask = oldMask;
                camera.clearFlags = CameraClearFlags.Depth;
                camera.depth = oldDepth;

                camera.transform.parent = uiRig.transform;
                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;

                // Set all canvas scalers to static, which makes UI better usable
                FindObjectsOfType<uGUI_CanvasScaler>().Where(obj => !obj.name.Contains("PDA")).ForEach(cs => cs.vrMode = uGUI_CanvasScaler.Mode.Static);
                SetupPDA();
                VrQuickSlots = new GameObject("VRQuickSlots").ResetTransform().AddComponent<VRQuickSlots>();
                VrQuickSlots.Setup(SteamVR_Actions.subnautica_OpenQuickSlotWheel);
            }
            else
            {
                camera.transform.parent = uiRig.transform;
                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;
            }
            uiCamera = camera;
            VRHud.Setup(uiCamera, rightControllerUI.transform);
        }

        void SetupPDA()
        {
            // Move the quickslots to bottom of PDA bottom left and make it bigger
            var pda = uGUI_PDA.main;
            var targetParent = pda.tabInventory.transform;
            var qs = FindObjectOfType<uGUI_QuickSlots>();
            var qstf = qs.transform;

            qstf.parent = targetParent;
            qstf.localPosition = new Vector3(-250, -455, 4f);
            qstf.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            qstf.localRotation = Quaternion.identity;

            // Add Pasuse Menu Button to PDA to PDA
            var dialog = pda.GetComponentInChildren<uGUI_Dialog>(true);
            var buttonPrefab = dialog.buttonPrefab;
            var button = Object.Instantiate(buttonPrefab, targetParent).GetComponent<uGUI_DialogButton>();
            button.button.transform.parent = targetParent;
            button.button.gameObject.gameObject.name = "PauseMenuButton";
            button.text.text = "Pause Menu";
            button.button.onClick.RemoveAllListeners();
            button.button.onClick.AddListener(() =>
            {
                IngameMenu.main.Open();
            });
            // Move it to the bottom right
            button.rectTransform.anchoredPosition = new Vector2(1100, 50);
            button.rectTransform.pivot = new Vector2(1, 0);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 300);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 100);
            button.rectTransform.ForceUpdateRectTransforms();
            button.rectTransform.GetComponentsInChildren<RectTransform>().ForEach(rt => rt.ForceUpdateRectTransforms());
        }

        public IEnumerator SetupGameCameras()
        {
            var rig = VRCameraRig.instance;
            rig.StealCamera(SNCameraRoot.main.mainCamera);
            yield return new WaitForSeconds(1.0f);
            rig.StealUICamera(SNCameraRoot.main.guiCamera, true);
            yield return new WaitForSeconds(0.1f);

            FindObjectsOfType<uGUI_CanvasScaler>().ForEach(cs => cs.SetDirty());
        }

        public void LateUpdate()
        {
            // Move the camera rig to the player each frame and rotate the uiRig accordingly
            // TODO: This probably has to be changed for roomscale tracking.
            // Right now if you move too far away from the center, you will rotate the camera with the center as a pivot.
            if (rigParentTarget != null)
            {
                Vector3 pos = rigParentTarget.position;
                // A large per-frame jump (teleport, vehicle enter) re-seeds
                // the smoothing instead of gliding there
                if (hasLastTargetPos && Vector3.Distance(pos, lastTargetPos) > 1f)
                {
                    smoothedRigY = float.NaN;
                    vehicleWaveNotch.Reset();
                    // Don't treat the teleport as fast motion
                    hasLastRawY = false;
                }
                // Vehicle enter/exit: reset and seed the notch on enter,
                // re-seed the low-pass on exit
                bool inVehicle = Player.main != null && Player.main.currentMountedVehicle != null;
                if (inVehicle != wasInVehicle)
                {
                    wasInVehicle = inVehicle;
                    vehicleWaveNotch.Reset();
                    if (inVehicle)
                    {
                        vehicleWaveNotch.Seed(pos.y);
                    }
                    else
                    {
                        smoothedRigY = float.NaN;
                    }
                }
                // Per-frame raw Y delta: bursts the trace on fast vertical
                // motion (walking gait, vehicle heave) to capture its shape
                bool fastMove = hasLastRawY && Mathf.Abs(pos.y - lastRawY) > 0.004f;
                lastRawY = pos.y;
                hasLastRawY = true;
                lastTargetPos = pos;
                hasLastTargetPos = true;
                ApplyYSmoothing(ref pos.y, inVehicle);
                this.transform.SetPositionAndRotation(pos, rigParentTarget.rotation);

                // The render camera is not smoothed on its own: with
                // cameraBobbing forced off it would only double the lag
                // (the wave enters the view through the rig channel alone)
                float camY = float.NaN;
                if (vrCamera != null && vrCamera.transform.parent == transform)
                {
                    camY = vrCamera.transform.position.y;
                }

                uiRig.transform.rotation = transform.rotation;

                if (Settings.IsDebugEnabled)
                {
                    // Dense per-frame burst of the smoothing state, on two
                    // triggers: a persistent raw/smoothed offset (not
                    // explained by the low-pass) or fast vertical motion
                    // (walking gait / vehicle heave, to measure its shape)
                    bool burst = bobBurstFrames > 0;
                    if (!burst && bobBurstCooldown <= 0)
                    {
                        bool offsetBurst = !float.IsNaN(smoothedRigY)
                            && Mathf.Abs(rigParentTarget.position.y - smoothedRigY) > 0.05f;
                        if (offsetBurst)
                        {
                            burst = true;
                            bobBurstFrames = 120;
                            Mod.logger.LogInfo($"[WalkBob] burst start (offset), offset={rigParentTarget.position.y - smoothedRigY:0.###}");
                        }
                        else if (fastMove)
                        {
                            burst = true;
                            bobBurstFrames = 120;
                            Mod.logger.LogInfo($"[WalkBob] burst start (fast move)");
                        }
                    }
                    if (burst)
                    {
                        bobBurstFrames--;
                        if (bobBurstFrames == 0)
                        {
                            bobBurstCooldown = 600;
                            Mod.logger.LogInfo("[WalkBob] burst end");
                        }
                        Mod.logger.LogInfo($"[WalkBob] s={Settings.VerticalSmoothing:0.##} veh={(inVehicle ? 1 : 0)} rawY={rigParentTarget.position.y:0.###} smooth={smoothedRigY:0.###} ts={Time.timeScale:0.#} dt={Time.unscaledDeltaTime:0.###}");
                    }
                    else if (bobBurstCooldown > 0)
                    {
                        bobBurstCooldown--;
                    }
                    if (Time.frameCount - lastBobLogFrame >= 30)
                    {
                        lastBobLogFrame = Time.frameCount;
                        // rawY is the unsmoothed target: compare to rigY to
                        // measure the actual damping in game
                        Mod.logger.LogInfo($"[WalkBob] s={Settings.VerticalSmoothing:0.##} veh={(inVehicle ? 1 : 0)} rawY={rigParentTarget.position.y:0.###} rigY={pos.y:0.###} camY={camY:0.###}");
                    }
                }
                /*TODO
                                RecenterBodyOnCameraOrientation(35f, 0.3f, 3.0f, 1.5f);  

                                float zOffset = Player.main?.inSeatruckPilotingChair == true || Player.main?.inExosuit == true ? -0.2f : -0.08f;                 
                                float yOffset = Player.main?.inSeatruckPilotingChair == true || Player.main?.inExosuit == true ? -0.2f : -0.1f;    
                                if(Player.main._cinematicModeActive == false)  
                                {
                                    Player.main.armsController.transform.position = SNCameraRoot.main.mainCamera.transform.position + (SNCameraRoot.main.mainCamera.transform.forward * zOffset) + new Vector3(0f, yOffset, 0f);
                                    //Player.main.armsController.transform.position = MainCameraControl.main.transform.position + (MainCameraControl.main.mainCamera.transform.forward * zOffset) + new Vector3(0f, yOffset, 0f);
                                }  
                */
            }

            // HandReticle is recreated on save/level load, possibly after
            // VRHud.Setup already ran (the split was then skipped): re-apply
            // the laser pointer setup whenever a new reticle instance shows
            if (Settings.PutHandReticleOnLaserPointer && HandReticle.main != null
                && ReticleSplit.SplitRoot != HandReticle.main.transform)
            {
                VRHud.SetupHandReticle(true);
            }
        }

        // Damps the vertical movement. On foot: exponential low-pass, the
        // lag is VerticalSmoothing * MaxSmoothingLag (damps the walking
        // step). In a vehicle: a 2nd-order notch on the wave heave band
        // (depth = the slider) - the vehicle's own motion (dive, terrain
        // follow) passes with ~zero lag so the view stays glued to it
        void ApplyYSmoothing(ref float y, bool inVehicle)
        {
            float s = Settings.VerticalSmoothing;
            if (s <= 0f)
            {
                return;
            }
            if (inVehicle)
            {
                float notched = vehicleWaveNotch.Process(y, Time.unscaledDeltaTime);
                y += s * (notched - y);
                return;
            }
            if (float.IsNaN(smoothedRigY))
            {
                smoothedRigY = y;
                return;
            }
            // Snap on teleports (level load)
            if (Mathf.Abs(y - smoothedRigY) > 2f)
            {
                smoothedRigY = y;
                return;
            }
            // Unscaled: Time.deltaTime is 0 while paused (PDA open), which
            // would freeze the smoothing mid-offset
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / (s * MaxSmoothingLag));
            smoothedRigY += (y - smoothedRigY) * k;
            y = smoothedRigY;
        }

        void DebugRaycasts()
        {
            if (false && Settings.IsDebugEnabled)
            {
                RaycastResult? uiTarget = fpsInput?.lastRaycastResult;
                DebugPanel.Show($"World Target: {worldTarget?.name}({worldTargetDistance})\nUI Target:{uiTarget?.gameObject?.name}({uiTarget?.distance})\nFocused: {EventSystem.current.isFocused}");
            }
        }

        // Gets set by GUIHand Patch, which already does world raycasting so we dont have to do it ourselfs
        public void SetWorldTarget(GameObject activeTarget, float activeHitDistance)
        {
            this.worldTarget = activeTarget;
            this.worldTargetDistance = activeHitDistance;
            this.worldTargetTime = Time.unscaledTime;
            this.laserPointerUI.SetWorldTarget(worldTarget, worldTargetDistance);
        }

        // True while the aim is on a targetable object, with the same 0.5 s
        // timeout the laser pointer uses for its dot
        public bool HasWorldTarget()
        {
            return worldTarget != null && Time.unscaledTime - worldTargetTime < 0.5f;
        }
    }

    #region Patches

    //Head based vs Hand based movement
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.forwardReference), MethodType.Getter)]
    public static class MoveDirectionOverride
    {
        public static Transform controllerTransform;
        static bool Prefix(PlayerController __instance, ref Transform __result)
        {
            if (Settings.HandBasedTurning)
            {
                //Use the Camera's position and the controller's rotation with a fixed pitch offset.
                //The laser pointer transform carries the per-tool aim offset (VRHands.OnToolEquipped),
                //which would make the movement axis jump whenever the equipped tool changes.
                var controller = Settings.LeftHandBasedTurning ? VRCameraRig.instance?.leftController : VRCameraRig.instance?.rightController;
                if (controller == null)
                {
                    __result = MainCamera.camera.transform;
                    return false;
                }
                //Use a dummy object to hold the transform
                if (controllerTransform == null)
                {
                    controllerTransform = new GameObject().transform;
                }
                controllerTransform.position = MainCamera.camera.transform.position;
                controllerTransform.rotation = controller.transform.rotation * Quaternion.Euler(Settings.HandMovementPitchOffset, 0f, 0f);
                __result = controllerTransform;
            }
            else
            {
                __result = MainCamera.camera.transform;
            }
            return false;
        }
    }

// NOTE: Not sure if needed, I already apply offsets on entering for vehicles.
#if false
    //Adjust the player position while piloting vehicles with vr offset positions and user overrides
    [HarmonyPatch(typeof(MainCameraControl), nameof(MainCameraControl.OnUpdate))]
    public static class PlayerPositionFixer
    {
        static void Postfix(MainCameraControl __instance)
        {
            bool inSeamoth = Player.main?.inSeamoth == true;
            bool inExosuit = Player.main?.inExosuit == true;
            bool inCyclops = Player.main?.currentSub?.isCyclops == true && Player.main?.isPiloting == true;

            float zOffset = 0.0f;
            float yOffset = 0.0f;
            if (inSeamoth)
            {
                zOffset += 0.0f + Settings.SeamothZOffset;
                yOffset += 0.07f + Settings.SeamothYOffset;
            }
            else if (inCyclops)
            {
                zOffset += 0.03f + Settings.CyclopsZOffset;
                yOffset += 0.08f + Settings.CyclopsYOffset;
            }
            else if (inExosuit)
            {
                zOffset += 0.03f + Settings.ExosuitZOffset;
                yOffset += 0.2f + Settings.ExosuitYOffset;
            }

            __instance.cameraUPTransform.localPosition = new Vector3(__instance.cameraUPTransform.localPosition.x, yOffset, zOffset);
            //__instance.cameraUPTransform.localRotation = Quaternion.Euler( new Vector3(0.0f, 0.0f, 0.0f));
        }
    }
#endif

    // Create the Rig together with the uGUI Prefab
    [HarmonyPatch(typeof(uGUI), nameof(uGUI.Awake))]
    public static class uGUI_AwakeSetupRig
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            // TODO: Should use proper singleton pattern?
            var rig = new GameObject(nameof(VRCameraRig)).AddComponent<VRCameraRig>();
            VRCameraRig.instance = rig;
            Object.DontDestroyOnLoad(rig);
        }
    }

    // Make the uGUI_GraphicRaycaster take the LaserPointers EventCamera when possible
    // Have to switch between guiCameraSpace and Worldspace for e.g. Scanner Room and Cyclops UI
    [HarmonyPatch(typeof(uGUI_GraphicRaycaster))]
    [HarmonyPatch(nameof(uGUI_GraphicRaycaster.eventCamera), MethodType.Getter)]
    class uGUI_GraphicRaycaster_VREventCamera_Patch
    {
        public static bool Prefix(uGUI_GraphicRaycaster __instance, ref Camera __result)
        {
            if (VRCameraRig.instance == null)
            {
                return true;
            }
            if (!(SNCameraRoot.main != null))
            {
                __result = VRCameraRig.instance.UIControllerCamera;
            }
            else
            {
                if (__instance.guiCameraSpace)
                {
                    __result = VRCameraRig.instance.UIControllerCamera;
                }
                else
                {
                    __result = VRCameraRig.instance.WorldControllerCamera;
                }
            }
            return false;
        }
    }

    // Same Patch as above but for the UnityEngine GraphicRaycaster
    // Turns out some canvases like the left panel on the cyclops don't use the uGUI_GraphicRaycaster
    // TODO: They seem to be in world space only though, have to double check.
    [HarmonyPatch(typeof(GraphicRaycaster))]
    [HarmonyPatch(nameof(GraphicRaycaster.eventCamera), MethodType.Getter)]
    class Unity_GraphicRaycaster_VREventCamera_Patch
    {
        public static bool Prefix(GraphicRaycaster __instance, ref Camera __result)
        {
            // TODO: Clean this up
            var canvas = __instance.GetComponent<Canvas>();
            if (canvas == null)
            {
                return true;
            }
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay || (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == null))
            {
                return true;
            }
            if (VRCameraRig.instance == null)
            {
                return true;
            }
            __result = VRCameraRig.instance.WorldControllerCamera;
            return false;
        }
    }

    [HarmonyPatch(typeof(uGUI_CanvasScaler), nameof(uGUI_CanvasScaler.UpdateTransform))]
    static class uGUI_CanvasScalerPDA_Attach
    {
        public static void Postfix(uGUI_CanvasScaler __instance)
        {
            // TODO: There gotta be a better way to attach this only to the PDA, maybe custom behaviour, disabling the Scalar?
            if (__instance.gameObject.GetComponent<uGUI_PDA>() == null)
            {
                return;
            }
            if (VRCameraRig.instance == null)
                return;
            var rigWorldPos = SNCameraRoot.main.transform;

            var worldPos = __instance._anchor.transform.position;
            var worldRot = __instance._anchor.transform.rotation;
            var uiSpacePos = worldPos - rigWorldPos.position;
            var uiSpaceRotation = worldRot;
            // DebugPanel.Show($"PDA Pos/Rot: {worldPos}/{worldRot.eulerAngles}\n -> {uiSpacePos}/{uiSpaceRotation.eulerAngles}\nrigPos/rot: {rigWorldPos.position}, {rigWorldPos.eulerAngles}");
            __instance.rectTransform.position = uiSpacePos;
            __instance.rectTransform.rotation = uiSpaceRotation;
        }
    }

    // Makes the ingame menu spawn infront of you in vr
    [HarmonyPatch(typeof(IngameMenu), nameof(IngameMenu.Awake))]
    class MakeIngameMenuStatic
    {
        public static void Postfix(IngameMenu __instance)
        {
            var scalar = __instance.GetComponent<uGUI_CanvasScaler>();
            scalar.vrMode = uGUI_CanvasScaler.Mode.Static;
        }
    }

    // Makes the builder menu spawn infront of you in vr
    // TODO: Could make those more general patches?
    [HarmonyPatch(typeof(uGUI_BuilderMenu), nameof(uGUI_BuilderMenu.Awake))]
    class MakeBuilderMenuStatic
    {
        public static void Postfix(uGUI_BuilderMenu __instance)
        {
            var scalar = __instance.GetComponent<uGUI_CanvasScaler>();
            scalar.vrMode = uGUI_CanvasScaler.Mode.Static;
        }
    }

    // Makes the builder menu spawn infront of you in vr
    [HarmonyPatch(typeof(uGUI_BuilderMenu), nameof(uGUI_BuilderMenu.Open))]
    class MakeBuilderMenuStatic2
    {
        public static void Postfix(uGUI_BuilderMenu __instance)
        {
            var scalar = __instance.GetComponent<uGUI_CanvasScaler>();
            scalar.SetDirty();
            scalar.UpdateTransform(SNCameraRoot.main.guiCamera);
        }
    }

    // Create the VRCameraRig when ArmsController is started
    [HarmonyPatch(typeof(ArmsController), nameof(ArmsController.Start))]
    public static class ArmsController_Start_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ArmsController __instance)
        {
            Camera mainCamera = SNCameraRoot.main.mainCam;
            VRCameraRig.instance.SetCameraTrackTarget(mainCamera.transform.parent);
            CoroutineHost.StartCoroutine(VRCameraRig.instance.SetupGameCameras());
        }
    }

    // Don't disable the the automatic camera tracking of the UI Camera in the Main Game
    [HarmonyPatch(typeof(ManagedCanvasUpdate), nameof(ManagedCanvasUpdate.GetUICamera))]
    public static class PatchCameraTrackingDisabled
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions).MatchForward(false, new CodeMatch[] {
                new CodeMatch(ci => ci.Calls(typeof(XRDevice).GetMethod(nameof(XRDevice.DisableAutoXRCameraTracking))))
            }).ThrowIfNotMatch("Could not find XRDevice Deactivation").Advance(-2).RemoveInstructions(3).InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(uGUI), nameof(uGUI.UpdateLevelIdentifier))]
    static class OnMainLevelChanged
    {
        public static void Postfix(uGUI __instance)
        {
            VRCameraRig.instance?.UpdateShowControllers();
        }
    }

    #endregion
}
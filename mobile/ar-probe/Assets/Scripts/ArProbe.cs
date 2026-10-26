using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SpatialTracking;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

// Shared plane tracking for the standalone probe and inspection app.
public sealed class ArProbe : MonoBehaviour
{
    public bool inspectionMode;
    private IssueReportForm reportForm;
    public Material markerMaterial;
    public Material planeMaterial;
    private ARSession session;
    private ARInputManager inputManager;
    private ARPlaneManager planes;
    private ARRaycastManager raycasts;
    private ARAnchorManager anchors;
    private AROcclusionManager occlusion;
    private GameObject originRoot, cameraHud;
    private Coroutine starting;
    private bool arReady;
    private string viewingKey;
    public bool CameraActive { get; private set; }
    private bool useDepth;
    public const string DepthPreference = "arwork.depth-assisted";
    public bool DepthAssistanceEnabled => PlayerPrefs.GetInt(DepthPreference, 0) == 1;
    private ARAnchor markerAnchor;
    private GameObject marker;
    private Text status;
    private Text notice;
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private Vector3 lastPosition;
    private readonly Dictionary<string, ARAnchor> savedMarkers = new Dictionary<string, ARAnchor>();
    private readonly Dictionary<string, string> markerStatuses = new Dictionary<string, string>();
    private GameObject suspendedMarker;
    private ARAnchor suspendedAnchor;
    private Vector3 suspendedPosition;

    private void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = inspectionMode ? 30 : 60;
        useDepth = DepthAssistanceEnabled;
        if (inspectionMode)
        {
            var background = new GameObject("Page background").AddComponent<Camera>();
            background.cullingMask = 0; background.depth = -10;
            background.clearFlags = CameraClearFlags.SolidColor;
            background.backgroundColor = new Color(0.02f, 0.05f, 0.08f);
        }
        CreateUi();
        var sessionObject = new GameObject("AR Session");
        sessionObject.SetActive(false);
        session = sessionObject.AddComponent<ARSession>();
        if (inspectionMode) session.matchFrameRateRequested = false;
        session.attemptUpdate = false;
        session.enabled = false;
        inputManager = sessionObject.AddComponent<ARInputManager>();
        inputManager.enabled = !inspectionMode;
        sessionObject.SetActive(true);

        var originObject = new GameObject("XR Origin");
        originObject.SetActive(false);
        var origin = originObject.AddComponent<XROrigin>();
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(originObject.transform, false);
        var cameraObject = new GameObject("AR Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(offset.transform, false);
        var camera = cameraObject.AddComponent<Camera>();
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 100f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        cameraObject.AddComponent<ARCameraManager>();
        cameraObject.AddComponent<ARCameraBackground>();
        if (inspectionMode)
        {
            occlusion = cameraObject.AddComponent<AROcclusionManager>();
            occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;
            occlusion.environmentDepthTemporalSmoothingRequested = true;
            // Depth is used for hit testing; keep the marker visible for inspection.
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
            occlusion.enabled = false;
        }
        var pose = cameraObject.AddComponent<TrackedPoseDriver>();
        pose.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice, TrackedPoseDriver.TrackedPose.Center);
        pose.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        pose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        origin.Camera = camera;
        origin.CameraFloorOffsetObject = offset;
        planes = originObject.AddComponent<ARPlaneManager>();
        planes.requestedDetectionMode = inspectionMode
            ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical : PlaneDetectionMode.Horizontal;
        planes.planesChanged += OnPlanesChanged;
        raycasts = originObject.AddComponent<ARRaycastManager>();
        anchors = originObject.AddComponent<ARAnchorManager>();
        originRoot = originObject;
        if (inspectionMode)
        {
            var images = originObject.AddComponent<ARTrackedImageManager>();
            images.enabled = false;
            card = originObject.AddComponent<CardLocalization>();
            card.Initialize(images);
        }
        originObject.SetActive(!inspectionMode);
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
    }

    private CardLocalization card;
    private ReferenceTarget selectedTarget;
    public bool IsEnrolling { get; private set; }
    public void UseReference(ReferenceTarget target) { selectedTarget = target; }
    public void BeginEnrollment() { IsEnrolling = true; EnterCamera(); }
    public IEnumerator ValidateReferenceImage(Texture2D image, float width, System.Action<bool> done) => card.ValidateImage(image, width, done);
    public Camera SceneCamera => originRoot.GetComponentInChildren<Camera>(true);
    private CardReference viewingReference;
    private GameObject restoredMarker;
    public bool IsViewing => viewingReference != null;
    public bool TryCaptureReference(out CardReference reference)
    {
        reference = null;
        if (card == null || !card.TryPose(out var pose) || !HasTrackedDraft) return false;
        reference = ReferenceFrame.Capture(selectedTarget, pose, CurrentPosition);
        return reference.IsSupported;
    }
    public void BeginView(CardReference reference, string issueStatus, string server, string id)
    {
        if (reference == null || !reference.IsSupported) return;
        viewingReference = reference; viewingKey = server + "/" + id;
        EnterCamera();
        restoredMarker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        restoredMarker.name = "Restored inspection point";
        restoredMarker.transform.localScale = Vector3.one * 0.1f;
        restoredMarker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
        SetMarkerColor(restoredMarker.GetComponent<Renderer>(), MarkerColor(issueStatus));
        Destroy(restoredMarker.GetComponent<Collider>());
        restoredMarker.SetActive(false);
    }

    private IEnumerator Start()
    {
        if (!inspectionMode) yield return StartAr();
    }

    public void EnterCamera()
    {
        if (CameraActive) return;
        CameraActive = true; arReady = false;
        card?.Clear();
        cameraHud.SetActive(true);
        GetComponent<IssueLiveUpdates>()?.ShowLabel(false);
        starting = StartCoroutine(StartAr());
    }

    public void LeaveCamera()
    {
        CameraActive = false; arReady = false; viewingKey = null;
        IsEnrolling = false;
        card?.Clear();
        viewingReference = null;
        if (restoredMarker != null) Destroy(restoredMarker);
        restoredMarker = null;
        // No old-session anchors may become visible when entering a new camera session.
        if (inspectionMode)
        {
            foreach (var anchor in savedMarkers.Values) if (anchor != null) Destroy(anchor.gameObject);
            savedMarkers.Clear();
            if (marker != null) Destroy(marker);
            if (markerAnchor != null) Destroy(markerAnchor.gameObject);
            marker = null; markerAnchor = null;
            reportForm?.ClearPosition();
        }
        if (starting != null) StopCoroutine(starting);
        starting = null;
        session.enabled = false;
        originRoot.SetActive(false);
        inputManager.enabled = false;
        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager != null && manager.isInitializationComplete) manager.StopSubsystems();
        cameraHud.SetActive(false);
        GetComponent<IssueLiveUpdates>()?.ShowLabel(true);
        Debug.Log("ARWORK_CAMERA_ACTIVE=false");
    }

    private IEnumerator StartAr()
    {
        notice.text = "正在准备相机和定位服务…";
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            bool answered = false;
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => answered = true;
            callbacks.PermissionDenied += _ => answered = true;
            callbacks.PermissionDeniedAndDontAskAgain += _ => answered = true;
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera, callbacks);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)
                   && !answered && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                notice.text = "未获得相机权限，可返回查看记录；允许权限后重新进入采集。";
                yield break;
            }
        }
#endif
        var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null) { notice.text = "此设备无法启动定位服务，可返回查看记录。"; yield break; }
        if (!manager.isInitializationComplete) yield return manager.InitializeLoader();
        if (inspectionMode && !CameraActive) yield break;
        if (manager.activeLoader == null) { notice.text = "此设备无法启动定位服务，可返回查看记录。"; yield break; }
        manager.StartSubsystems();
        inputManager.enabled = true;
        originRoot.SetActive(true);
        ApplyDepth();
        Debug.Log("ARWORK_CAMERA_ACTIVE=true");
        yield return ARSession.CheckAvailability();
        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            notice.text = "定位服务需要安装或更新，请按系统提示操作。";
            yield return ARSession.Install();
        }
        if (ARSession.state == ARSessionState.Ready || ARSession.state == ARSessionState.SessionInitializing
            || ARSession.state == ARSessionState.SessionTracking)
        {
            session.enabled = true;
            if (inspectionMode) { session.Reset(); if (!IsEnrolling && selectedTarget != null) yield return card.Prepare(selectedTarget); }
            arReady = true;
            notice.text = "缓慢移动手机扫描地面或墙面，点击表面放置标记。";
        }
        else
        {
            notice.text = "定位服务未就绪，请检查网络和系统定位服务后重启应用。";
        }
    }

    private void OnPlanesChanged(ARPlanesChangedEventArgs changes)
    {
        foreach (var plane in changes.added)
        {
            plane.gameObject.AddComponent<MeshFilter>();
            var renderer = plane.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = planeMaterial;
            if (inspectionMode)
            {
                var tint = new MaterialPropertyBlock();
                tint.SetColor("_Color", new Color(0.1f, 0.8f, 0.35f, 0.08f));
                renderer.SetPropertyBlock(tint);
            }
            var visualizer = plane.gameObject.AddComponent<ARPlaneMeshVisualizer>();
            visualizer.trackingStateVisibilityThreshold = TrackingState.Tracking;
            visualizer.hideSubsumed = true;
        }
    }

    private void Update()
    {
        if (inspectionMode && (!CameraActive || !arReady)) return;
        foreach (var plane in planes.trackables)
        {
            // The visualizer owns renderer visibility. Disable it when the whole session loses tracking.
            var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
            if (visualizer != null) visualizer.enabled = ARSession.state == ARSessionState.SessionTracking && plane.subsumedBy == null;
        }
        status.text = ARSession.state == ARSessionState.SessionTracking ? "园区巡检 · 定位正常" : "园区巡检 · 正在识别环境";
        if (inspectionMode && useDepth)
            status.text += occlusion.currentEnvironmentDepthMode == EnvironmentDepthMode.Disabled
                ? " · 深度准备中/不可用" : " · 深度辅助已启用";
        if (inspectionMode)
        {
            if (IsEnrolling) { notice.text = "正对固定标牌，使图案四边贴合取景框；不要拍摄整个房间。"; return; }
            bool tracked = card.TryPose(out var cardPose);
            if (viewingReference != null)
            {
                if (restoredMarker != null)
                {
                    restoredMarker.SetActive(tracked);
                    if (tracked) restoredMarker.transform.position = ReferenceFrame.ToWorld(cardPose, viewingReference.Point);
                }
                notice.text = tracked ? "已恢复相对点位；参照如被移动，位置将不可信，须换版本重新采集。" : card.Message + " 跟踪失效时隐藏点位，请重新扫描。";
                return;
            }
            if (marker != null) marker.SetActive(tracked && markerAnchor != null && markerAnchor.trackingState == TrackingState.Tracking);
            notice.text = tracked ? "参照图案跟踪正常，可点击表面选点。参照被挪动须换版本重新采集。" : card.Message + " 跟踪失效时不能选点或拍摄，请重新扫描。";
            if (!tracked) return;
        }
        if (ARSession.state != ARSessionState.SessionTracking || Input.touchCount == 0) return;
        if (reportForm != null && reportForm.PositionLocked) return;
        var touch = Input.GetTouch(0);
        if (touch.phase != TouchPhase.Began) return;
        var pointer = new PointerEventData(EventSystem.current) { position = touch.position };
        uiHits.Clear();
        EventSystem.current.RaycastAll(pointer, uiHits);
        if (uiHits.Count != 0) return;

        if (useDepth && occlusion != null && occlusion.currentEnvironmentDepthMode != EnvironmentDepthMode.Disabled
            && raycasts.Raycast(touch.position, hits, TrackableType.Depth))
        {
            foreach (var hit in hits)
            {
                // This is the documented useful range, not a claim of measurement accuracy.
                if (hit.distance < 0.5f || hit.distance > 5f) continue;
                var anchorObject = new GameObject("Depth inspection anchor");
                anchorObject.transform.SetPositionAndRotation(hit.pose.position, hit.pose.rotation);
                var depthAnchor = anchorObject.AddComponent<ARAnchor>();
                if (depthAnchor.trackableId == TrackableId.invalidId || depthAnchor.trackingState != TrackingState.Tracking)
                {
                    Destroy(anchorObject);
                    continue;
                }
                PlaceMarker(depthAnchor, hit, "Depth");
                return;
            }
        }

        if (!raycasts.Raycast(touch.position, hits, TrackableType.PlaneWithinPolygon))
        {
            notice.text = "暂未找到可用位置，请缓慢移动并对准有纹理的表面。";
            return;
        }
        foreach (var hit in hits)
        {
            var plane = planes.GetPlane(hit.trackableId);
            if (plane == null || (plane.alignment != PlaneAlignment.HorizontalUp &&
                !(inspectionMode && plane.alignment == PlaneAlignment.Vertical))
                || plane.trackingState != TrackingState.Tracking) continue;
            var newAnchor = anchors.AttachAnchor(plane, hit.pose);
            if (newAnchor == null)
            {
                notice.text = "未能固定标记，请重新扫描后选点。";
                return;
            }
            PlaceMarker(newAnchor, hit, plane.alignment == PlaneAlignment.Vertical ? "Wall plane" : "Floor plane");
            return;
        }
        notice.text = "请点击已识别的地面或墙面。";
    }

    public void SetDepthAssistance(bool enabled)
    {
        useDepth = enabled;
        PlayerPrefs.SetInt(DepthPreference, enabled ? 1 : 0); PlayerPrefs.Save();
        ApplyDepth();
    }

    private void ApplyDepth()
    {
        if (occlusion == null) return;
        occlusion.requestedEnvironmentDepthMode = useDepth ? EnvironmentDepthMode.Fastest : EnvironmentDepthMode.Disabled;
        occlusion.enabled = CameraActive && useDepth;
        Debug.Log("ARWORK_DEPTH_REQUESTED=" + useDepth + " camera=" + CameraActive);
    }

    private void PlaceMarker(ARAnchor newAnchor, ARRaycastHit hit, string source)
    {
        if (marker == null)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "10 cm inspection marker";
            marker.transform.localScale = Vector3.one * 0.1f;
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            SetMarkerColor(marker.GetComponent<Renderer>(), Color.yellow);
            Destroy(marker.GetComponent<Collider>());
        }
        lastPosition = hit.pose.position;
        marker.transform.SetParent(newAnchor.transform, false);
        marker.transform.localPosition = inspectionMode ? Vector3.zero : Vector3.up * 0.05f;
        marker.transform.localRotation = Quaternion.identity;
        if (markerAnchor != null) Destroy(markerAnchor.gameObject);
        markerAnchor = newAnchor;
        if (reportForm != null) reportForm.MarkPositionSelected();
        notice.text = "位置已选中，可继续点击调整。";
        Debug.Log("ARWORK_PLACED source=" + source + " distance=" + hit.distance.ToString("F2") + " position=" + lastPosition.ToString("F3"));
    }

    public Vector3 CurrentPosition => markerAnchor != null ? markerAnchor.transform.position : lastPosition;
    public bool HasTrackedDraft => markerAnchor != null && markerAnchor.trackingState == TrackingState.Tracking &&
        (!inspectionMode || (CameraActive && card != null && card.TryPose(out _)));

    public void KeepSubmittedMarker(string server, string id, bool replaceExisting = false)
    {
        var key = server + "/" + id;
        if (savedMarkers.TryGetValue(key, out var old) && old != null)
        {
            // An original creation receipt may arrive after a successful remark.
            // Acknowledging that receipt must not replace the newer session marker.
            if (!replaceExisting)
            {
                if (marker != null) Destroy(marker);
                if (markerAnchor != null) Destroy(markerAnchor.gameObject);
                marker = null; markerAnchor = null;
                return;
            }
            Destroy(old.gameObject);
        }
        if (markerAnchor != null)
        {
            savedMarkers[key] = markerAnchor;
            UpdateMarkerStatus(server, id, markerStatuses.TryGetValue(key, out var value) ? value : "open");
        }
        marker = null;
        markerAnchor = null;
    }

    private static void SetMarkerColor(Renderer renderer, Color color)
    {
        if (renderer == null) return;
        var tint = new MaterialPropertyBlock(); tint.SetColor("_Color", color);
        renderer.SetPropertyBlock(tint);
    }

    private static Color MarkerColor(string value) => value == "resolved" ? new Color(0.1f, 0.9f, 0.3f) :
        value == "in_progress" ? new Color(0.15f, 0.5f, 1f) : new Color(1f, 0.4f, 0.05f);

    public void UpdateMarkerStatus(string server, string id, string value)
    {
        var key = server + "/" + id;
        markerStatuses[key] = value;
        if (viewingKey == key && restoredMarker != null) SetMarkerColor(restoredMarker.GetComponent<Renderer>(), MarkerColor(value));
        if (!savedMarkers.TryGetValue(key, out var anchor) || anchor == null) return;
        SetMarkerColor(anchor.GetComponentInChildren<Renderer>(), MarkerColor(value));
    }

    public void BeginRemark()
    {
        suspendedMarker = marker; suspendedAnchor = markerAnchor; suspendedPosition = lastPosition;
        if (suspendedMarker != null) suspendedMarker.SetActive(false);
        marker = null; markerAnchor = null;
    }

    public void FinishRemark(bool saved, string server, string id)
    {
        if (saved) KeepSubmittedMarker(server, id, true);
        else
        {
            if (marker != null) Destroy(marker);
            if (markerAnchor != null) Destroy(markerAnchor.gameObject);
        }
        marker = suspendedMarker; markerAnchor = suspendedAnchor; lastPosition = suspendedPosition;
        if (marker != null) marker.SetActive(true);
        suspendedMarker = null; suspendedAnchor = null;
        notice.text = saved ? "新位置已保存。" : "已取消，原标记保留。";
    }

    private void CreateUi()
    {
        new GameObject("Event System", typeof(EventSystem), typeof(StandaloneInputModule));
        var canvasObject = new GameObject("Probe UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0f;
        var safe = new GameObject("Safe area", typeof(RectTransform)).GetComponent<RectTransform>();
        safe.SetParent(canvasObject.transform, false);
        var area = Screen.safeArea;
        safe.anchorMin = new Vector2(area.xMin / Screen.width, area.yMin / Screen.height);
        safe.anchorMax = new Vector2(area.xMax / Screen.width, area.yMax / Screen.height);
        safe.offsetMin = safe.offsetMax = Vector2.zero;
        var top = Panel(safe, "Status panel", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), 200);
        cameraHud = top.gameObject;
        status = Label(top, 30);
        status.rectTransform.offsetMin = new Vector2(24, 130);
        status.rectTransform.offsetMax = new Vector2(-24, -20);
        notice = Label(top, 28);
        notice.color = new Color(0.7f, 1f, 0.7f);
        notice.rectTransform.offsetMin = new Vector2(24, 12);
        notice.rectTransform.offsetMax = new Vector2(-24, -70);
        if (inspectionMode)
        {
            reportForm = gameObject.AddComponent<IssueReportForm>();
            reportForm.Initialize(safe, this);
            cameraHud.SetActive(false);
        }
    }

    private static RectTransform Panel(Transform parent, string name, Vector2 min, Vector2 max, Vector2 pivot, float height)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.88f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.pivot = pivot;
        rect.sizeDelta = new Vector2(0, height);
        rect.anchoredPosition = Vector2.zero;
        return rect;
    }

    private static Text Label(Transform parent, int size)
    {
        var label = new GameObject("Label", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        label.transform.SetParent(parent, false);
        label.font = InspectionUi.Font;
        label.fontSize = size;
        label.color = Color.white;
        label.raycastTarget = false;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        return label;
    }

    private static Button MakeButton(Transform parent, string text, float min, float max)
    {
        var buttonObject = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = new Color(0.1f, 0.28f, 0.36f);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(min, 0.15f);
        rect.anchorMax = new Vector2(max, 0.85f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var label = Label(buttonObject.transform, 30);
        label.text = text;
        label.alignment = TextAnchor.MiddleCenter;
        return buttonObject.GetComponent<Button>();
    }
}

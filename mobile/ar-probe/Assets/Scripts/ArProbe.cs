using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SpatialTracking;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// Shared plane tracking for the standalone probe and inspection app.
public sealed class ArProbe : MonoBehaviour
{
    public bool inspectionMode;
    private IssueReportForm reportForm;
    public Material markerMaterial;
    public Material planeMaterial;
    private ARSession session;
    private ARPlaneManager planes;
    private ARRaycastManager raycasts;
    private ARAnchorManager anchors;
    private AROcclusionManager occlusion;
    private bool useDepth = true;
    private Button depthToggle;
    private string lastHitSource = "none";
    private ARAnchor markerAnchor;
    private GameObject marker;
    private Text status;
    private Text notice;
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private int placements;
    private Vector3 lastPosition;
    private readonly Dictionary<string, ARAnchor> savedMarkers = new Dictionary<string, ARAnchor>();
    private GameObject suspendedMarker;
    private ARAnchor suspendedAnchor;
    private Vector3 suspendedPosition;

    private void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 60;
        CreateUi();
        var sessionObject = new GameObject("AR Session");
        sessionObject.SetActive(false);
        session = sessionObject.AddComponent<ARSession>();
        session.attemptUpdate = false;
        session.enabled = false;
        sessionObject.AddComponent<ARInputManager>();
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
            occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;
            occlusion.environmentDepthTemporalSmoothingRequested = true;
            // Depth is used for hit testing; keep the marker visible for inspection.
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
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
        originObject.SetActive(true);
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
    }

    private IEnumerator Start()
    {
        yield return StartAr();
    }

    private IEnumerator StartAr()
    {
        notice.text = "Checking camera permission and AR service...";
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                notice.text = "Camera permission is missing. Allow it in app settings, then exit and reopen this app.";
                yield break;
            }
        }
#endif
        yield return ARSession.CheckAvailability();
        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            notice.text = "AR service needs installation/update. Follow the system prompt.";
            yield return ARSession.Install();
        }
        if (ARSession.state == ARSessionState.Ready || ARSession.state == ARSessionState.SessionInitializing
            || ARSession.state == ARSessionState.SessionTracking)
        {
            session.enabled = true;
            notice.text = "Move slowly over a well-lit textured surface. Tap a detected floor or wall to place a cube.";
        }
        else
        {
            notice.text = "AR is not ready: " + ARSession.state + ". Check AR service and network, then exit and reopen this app.";
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
        int trackedPlanes = 0;
        int verticalPlanes = 0;
        foreach (var plane in planes.trackables)
        {
            // The visualizer owns renderer visibility. Disable it when the whole session loses tracking.
            var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
            if (visualizer != null) visualizer.enabled = ARSession.state == ARSessionState.SessionTracking && plane.subsumedBy == null;
            if (plane.trackingState == TrackingState.Tracking && plane.alignment == PlaneAlignment.HorizontalUp)
                trackedPlanes++;
            if (plane.trackingState == TrackingState.Tracking && plane.alignment == PlaneAlignment.Vertical)
                verticalPlanes++;
        }
        status.text = (inspectionMode ? "AR INSPECTION" : "AR DEVICE PROBE") + "\nSession: " + ARSession.state +
                      "\nTracking reason: " + ARSession.notTrackingReason +
                      "\nHorizontal: " + trackedPlanes + "  Vertical: " + verticalPlanes + "  Placements: " + placements +
                      (inspectionMode ? "\nDepth: " + DepthState() + "  Hit: " + lastHitSource : "") +
                      (marker ? "\nPosition (m): " + lastPosition.ToString("F2") : "\nMarker: none");
        if (depthToggle != null) depthToggle.interactable = reportForm == null || !reportForm.PositionLocked;
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
            notice.text = "No reliable hit. Move sideways near textured edges; try 0.5-5 m from the wall. Existing marker kept.";
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
                notice.text = "Could not attach an AR anchor. Scan the surface and try again.";
                return;
            }
            PlaceMarker(newAnchor, hit, plane.alignment == PlaneAlignment.Vertical ? "Wall plane" : "Floor plane");
            return;
        }
        notice.text = inspectionMode ? "Tap a tracked floor or wall." : "Tap a tracked upward-facing horizontal surface.";
    }

    private string DepthState()
    {
        if (!useDepth) return "OFF";
        if (occlusion == null || occlusion.descriptor == null) return "starting";
        if (occlusion.descriptor.environmentDepthImageSupported == Supported.Unsupported) return "unsupported (planes)";
        if (occlusion.currentEnvironmentDepthMode == EnvironmentDepthMode.Disabled) return "waiting (planes)";
        return occlusion.environmentDepthTexture != null ? "ready" : "enabled/no frame";
    }

    private void ToggleDepth()
    {
        if (reportForm != null && reportForm.PositionLocked) return;
        useDepth = !useDepth;
        occlusion.requestedEnvironmentDepthMode = useDepth ? EnvironmentDepthMode.Best : EnvironmentDepthMode.Disabled;
        depthToggle.GetComponentInChildren<Text>().text = useDepth ? "DEPTH AUTO" : "PLANE ONLY";
        notice.text = useDepth ? "Depth first, planes as fallback. Move sideways to gather depth; plain walls may still fail."
            : "Plane-only comparison. Scan textured floor/wall edges before tapping.";
    }

    private void PlaceMarker(ARAnchor newAnchor, ARRaycastHit hit, string source)
    {
        if (marker == null)
        {
            marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "10 cm inspection marker";
            marker.transform.localScale = Vector3.one * 0.1f;
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            Destroy(marker.GetComponent<Collider>());
        }
        lastPosition = hit.pose.position;
        marker.transform.SetParent(newAnchor.transform, false);
        marker.transform.localPosition = Vector3.up * 0.05f;
        marker.transform.localRotation = Quaternion.identity;
        if (markerAnchor != null) Destroy(markerAnchor.gameObject);
        markerAnchor = newAnchor;
        lastHitSource = source;
        placements++;
        if (reportForm != null) reportForm.MarkPositionSelected();
        notice.text = source + " hit at " + hit.distance.ToString("F2") + " m. Check alignment from another angle before reporting.";
        Debug.Log("ARWORK_PLACED source=" + source + " distance=" + hit.distance.ToString("F2") + " position=" + lastPosition.ToString("F3"));
    }

    private void ClearMarker()
    {
        if (reportForm != null && reportForm.PositionLocked) return;
        if (marker != null) Destroy(marker);
        if (markerAnchor != null) Destroy(markerAnchor.gameObject);
        lastHitSource = "none";
        if (reportForm != null) reportForm.ClearPosition();
        notice.text = "Marker cleared. Tap a detected surface to place it again.";
    }

    public Vector3 CurrentPosition => markerAnchor != null ? markerAnchor.transform.position : lastPosition;
    public bool HasTrackedDraft => markerAnchor != null && markerAnchor.trackingState == TrackingState.Tracking;

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
        if (markerAnchor != null) savedMarkers[key] = markerAnchor;
        marker = null;
        markerAnchor = null;
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
        notice.text = saved ? "Replacement saved. Previous session marker removed." : "Replacement draft discarded. Saved marker kept.";
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
        var top = Panel(safe, "Status panel", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), 390);
        status = Label(top, 30);
        status.rectTransform.offsetMin = new Vector2(24, 150);
        status.rectTransform.offsetMax = new Vector2(-24, -20);
        notice = Label(top, 28);
        notice.color = new Color(0.7f, 1f, 0.7f);
        notice.rectTransform.offsetMin = new Vector2(24, 12);
        notice.rectTransform.offsetMax = new Vector2(-24, -235);
        var bottom = Panel(safe, "Buttons", Vector2.zero, new Vector2(1, 0), new Vector2(0.5f, 0), 130);
        var clear = MakeButton(bottom, "CLEAR MARKER", 0.02f, inspectionMode ? 0.32f : 0.49f);
        clear.onClick.AddListener(ClearMarker);
        if (inspectionMode)
        {
            depthToggle = MakeButton(bottom, "DEPTH AUTO", 0.34f, 0.66f);
            depthToggle.onClick.AddListener(ToggleDepth);
        }
        var exit = MakeButton(bottom, "EXIT APP", inspectionMode ? 0.68f : 0.51f, 0.98f);
        exit.onClick.AddListener(() => Application.Quit());
        if (inspectionMode)
        {
            reportForm = gameObject.AddComponent<IssueReportForm>();
            reportForm.Initialize(safe, this);
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
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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

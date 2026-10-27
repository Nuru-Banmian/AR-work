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

// AR session and local-scan marker interaction. No plane detection or fallback plane placement.
public sealed class ArProbe : MonoBehaviour
{
    public bool inspectionMode;
    private IssueReportForm reportForm;
    public Material markerMaterial;
    public Material planeMaterial;
    private ARSession session;
    private ARInputManager inputManager;
    private AROcclusionManager occlusion;
    private ARCameraManager arCameraManager;
    private bool cameraFrameSeen;
    private double latestCameraTimestamp;
    private LocalSurfaceScanner surfaceScanner;
    private Transform safeUi;
    private GameObject originRoot, cameraHud;
    private Coroutine starting;
    public bool CameraActive { get; private set; }
    public bool SurfaceInteractionActive => surfaceScanner != null && surfaceScanner.BlocksPlacement;
    public void OpenSavedScans() { surfaceScanner?.ShowLibrary(); }
    private bool snapshotMode;
    public void SetSnapshotMode(bool enabled) {
        snapshotMode = enabled;
        surfaceScanner?.SetVisible(!enabled && CameraActive && reportForm != null && !reportForm.PositionLocked);

    }
    private ARAnchor markerAnchor;
    private GameObject marker;
    private Mesh pointedMarkerMesh;
    private Text status;
    private Text notice;
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
        arCameraManager = cameraObject.AddComponent<ARCameraManager>();
        arCameraManager.autoFocusRequested = true;
        arCameraManager.frameReceived += OnCameraFrame;
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
        originObject.AddComponent<ARAnchorManager>();
        originRoot = originObject;
        originObject.SetActive(!inspectionMode);
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
        if (inspectionMode)
        {
            surfaceScanner = gameObject.AddComponent<LocalSurfaceScanner>();
            surfaceScanner.Initialize(this, safeUi, camera, occlusion);
        }
    }

    private IEnumerator Start()
    {
        if (!inspectionMode) yield return StartAr();
    }

    public void EnterCamera()
    {
        if (CameraActive) return;
        CameraActive = true;
        cameraFrameSeen = false; latestCameraTimestamp = 0;
        cameraHud.SetActive(true);
        GetComponent<IssueLiveUpdates>()?.ShowLabel(false);
        starting = StartCoroutine(StartAr());
    }

    public void LeaveCamera()
    {
        CameraActive = false;
        surfaceScanner?.SetVisible(false);
        if (starting != null) StopCoroutine(starting);
        starting = null;
        // Stop camera/depth consumers before pausing the native AR session.
        if (occlusion != null) occlusion.enabled = false;
        originRoot.SetActive(false);
        session.enabled = false;
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
        // Keep the XR loader lifecycle symmetric; AR Foundation components own their subsystems.
        manager.StartSubsystems();
        inputManager.enabled = true;
        Debug.Log("ARWORK_START_STAGE=availability state="+ARSession.state);
        yield return ARSession.CheckAvailability();
        Debug.Log("ARWORK_START_STAGE=available state="+ARSession.state);
        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            notice.text = "定位服务需要安装或更新，请按系统提示操作。";
            yield return ARSession.Install();
        }
        if (ARSession.state == ARSessionState.Ready || ARSession.state == ARSessionState.SessionInitializing
            || ARSession.state == ARSessionState.SessionTracking)
        {
            // ARCore resume can synchronously wait for camera output after depth capture.
            // Prepare camera consumers first, then resume the native session in this same frame.
            if (inspectionMode && !CameraActive) yield break;
            Debug.Log("ARWORK_START_STAGE=consumers_enable");
            originRoot.SetActive(true);
            ApplyDepth();
            Debug.Log("ARWORK_START_STAGE=session_enable");
            session.enabled = true;
            Debug.Log("ARWORK_CAMERA_ACTIVE=true");
            notice.text = "请开始局部扫描，暂停后点击实物位置放标记。";
        }
        else
        {
            notice.text = "定位服务未就绪，请检查网络和系统定位服务后重启应用。";
        }
    }

    private void OnCameraFrame(ARCameraFrameEventArgs frame)
    {
        if (frame.timestampNs.HasValue) latestCameraTimestamp=frame.timestampNs.Value*1e-9;
        if (!CameraActive || cameraFrameSeen || frame.textures == null || frame.textures.Count == 0) return;
        cameraFrameSeen = true;
        var texture = frame.textures[0];
        Debug.Log("ARWORK_CAMERA_FRAME texture=" + texture.width + "x" + texture.height
            + " autofocus=" + arCameraManager.autoFocusEnabled + " configuration=" + arCameraManager.currentConfiguration);
    }

    private void Update()
    {
        if (inspectionMode && !CameraActive) return;
        surfaceScanner?.SetVisible(!snapshotMode && reportForm != null && !reportForm.PositionLocked);
        status.text = ARSession.state == ARSessionState.SessionTracking ? "园区巡检 · 定位正常" : "园区巡检 · 正在识别环境";
        if (surfaceScanner != null && surfaceScanner.RequiresDepth)
            status.text += occlusion.currentEnvironmentDepthMode == EnvironmentDepthMode.Disabled ? " · 等待深度" : " · 扫描深度已启用";
        if (ARSession.state != ARSessionState.SessionTracking || Input.touchCount == 0) return;
        if (reportForm != null && reportForm.PositionLocked) return;
        var touch = Input.GetTouch(0);
        if (touch.phase != TouchPhase.Began) return;
        var pointer = new PointerEventData(EventSystem.current) { position = touch.position };
        uiHits.Clear();
        EventSystem.current.RaycastAll(pointer, uiHits);
        if (uiHits.Count != 0) return;
        if (surfaceScanner != null && surfaceScanner.BlocksPlacement)
        {
            surfaceScanner.Pick(touch.position);
            return;
        }
        if (surfaceScanner != null && surfaceScanner.Raycast(touch.position, out var surfacePose))
        {
            TryPlacePose(surfacePose, "Reconstructed surface");
            return;
        }

        notice.text = "请先开始局部扫描，暂停后点击实物位置放标记。";
    }

    private void ApplyDepth()
    {
        if (occlusion == null) return;
        bool required = surfaceScanner != null && surfaceScanner.RequiresDepth;
        occlusion.requestedEnvironmentDepthMode = required ? EnvironmentDepthMode.Fastest : EnvironmentDepthMode.Disabled;
        occlusion.enabled = CameraActive && required;
        Debug.Log("ARWORK_DEPTH_REQUESTED=" + required + " camera=" + CameraActive);
    }

    public void RefreshDepth() { ApplyDepth(); }

    public bool TryDepthPose(Vector2 position, out Pose pose, out string reason)
    {
        pose=default; reason="请等待定位和深度数据稳定后再选中心。";
        if (ARSession.state!=ARSessionState.SessionTracking || occlusion==null
            || !arCameraManager.TryGetIntrinsics(out var intrinsics)
            || intrinsics.focalLength.x<=0 || intrinsics.focalLength.y<=0
            || !occlusion.TryAcquireSmoothedEnvironmentDepthCpuImage(out var image)) return false;
        using (image)
        {
            if (image.format!=XRCpuImage.Format.DepthUint16 || latestCameraTimestamp==0
                || System.Math.Abs(latestCameraTimestamp-image.timestamp)>0.1) return false;
            var camera=arCameraManager.GetComponent<Camera>();
            var cameraToWorld=SurfaceMeshData.NativeCameraToWorld(camera.transform.localToWorldMatrix,Screen.orientation);
            var direction=cameraToWorld.inverse.MultiplyVector(camera.ScreenPointToRay(position).direction);
            if (direction.z<=0) return false;
            float scale=Mathf.Max((float)image.width/intrinsics.resolution.x,(float)image.height/intrinsics.resolution.y);
            var focal=intrinsics.focalLength*scale;
            var principal=(intrinsics.principalPoint-(Vector2)intrinsics.resolution*0.5f)*scale
                +new Vector2(image.width,image.height)*0.5f-Vector2.one*0.5f;
            int u=Mathf.RoundToInt(focal.x*direction.x/direction.z+principal.x);
            int v=Mathf.RoundToInt(-focal.y*direction.y/direction.z+principal.y);
            if (u<1 || v<1 || u>=image.width-1 || v>=image.height-1) return false;
            var plane=image.GetPlane(0); var samples=new float[9]; int index=0;
            for (int y=v-1;y<=v+1;y++) for (int x=u-1;x<=u+1;x++)
            {
                int offset=y*plane.rowStride+x*plane.pixelStride;
                samples[index++]=(plane.data[offset]|plane.data[offset+1]<<8)*0.001f;
            }
            bool valid=SurfaceMeshData.TryMedianDepth(samples,out float metres,out reason);
            Debug.Log("ARWORK_SCAN_PICK valid="+valid+" depth="+metres.ToString("F3")+" pixel="+u+","+v);
            if (!valid) return false;
            var point=SurfaceMeshData.Unproject(u,v,metres,image.width,image.height,
                intrinsics.focalLength,intrinsics.principalPoint,intrinsics.resolution);
            pose=new Pose(cameraToWorld.MultiplyPoint3x4(point),Quaternion.identity); return true;
        }
    }

    private void TryPlacePose(Pose pose, string source)
    {
        var root = new GameObject("Surface inspection anchor");
        root.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var newAnchor = root.AddComponent<ARAnchor>();
        if (newAnchor.trackableId == TrackableId.invalidId || newAnchor.trackingState != TrackingState.Tracking)
        { Destroy(root); notice.text = "未能固定标记，请恢复跟踪后重试。"; return; }
        PlaceMarkerAt(newAnchor, pose.position, source);
    }

    private void PlaceMarkerAt(ARAnchor newAnchor, Vector3 position, string source)
    {
        if (marker == null)
        {
            marker = new GameObject("2 cm pointed inspection marker",typeof(MeshFilter),typeof(MeshRenderer));
            if (pointedMarkerMesh == null) pointedMarkerMesh=CreateMarkerMesh();
            marker.GetComponent<MeshFilter>().sharedMesh=pointedMarkerMesh;
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            SetMarkerColor(marker.GetComponent<Renderer>(), Color.yellow);
        }
        lastPosition = position;
        marker.transform.SetParent(newAnchor.transform, false);
        marker.transform.localPosition = Vector3.zero;
        marker.transform.localRotation = Quaternion.identity;
        if (markerAnchor != null) Destroy(markerAnchor.gameObject);
        markerAnchor = newAnchor;
        if (reportForm != null) reportForm.MarkPositionSelected();
        notice.text = "位置已选中，可继续点击调整。";
        Debug.Log("ARWORK_PLACED source=" + source + " position=" + lastPosition.ToString("F3"));
    }

    internal static Mesh CreateMarkerMesh()
    {
        var mesh=new Mesh { name="Pointed triangular marker" };
        // Local +Y is the outward surface normal. The apex at zero touches the selected point.
        mesh.vertices=new[] {Vector3.zero,new Vector3(-0.01f,0.025f,-0.006f),
            new Vector3(0.01f,0.025f,-0.006f),new Vector3(0,0.025f,0.012f)};
        mesh.triangles=new[] {0,1,2,0,2,3,0,3,1,1,3,2};
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }
    private void OnDestroy()
    {
        if (arCameraManager != null) arCameraManager.frameReceived-=OnCameraFrame;
        if (pointedMarkerMesh != null) Destroy(pointedMarkerMesh);
    }

    public Vector3 CurrentPosition => markerAnchor != null ? markerAnchor.transform.position : lastPosition;
    public bool HasTrackedDraft => markerAnchor != null && markerAnchor.trackingState == TrackingState.Tracking;

    public void ReaddressMarkers(string previous, string current)
    {
        foreach (var key in new System.Collections.Generic.List<string>(savedMarkers.Keys)) {
            if (!key.StartsWith(previous + "/", System.StringComparison.Ordinal)) continue;
            savedMarkers[current + key.Substring(previous.Length)] = savedMarkers[key]; savedMarkers.Remove(key);
        }
        foreach (var key in new System.Collections.Generic.List<string>(markerStatuses.Keys)) {
            if (!key.StartsWith(previous + "/", System.StringComparison.Ordinal)) continue;
            markerStatuses[current + key.Substring(previous.Length)] = markerStatuses[key]; markerStatuses.Remove(key);
        }
    }

    public void KeepQueuedMarker(string server, string key)
    {
        KeepSubmittedMarker(server, "local-" + key);
        if (savedMarkers.TryGetValue(server + "/local-" + key, out var anchor) && anchor != null)
            SetMarkerColor(anchor.GetComponentInChildren<Renderer>(), Color.yellow);
    }

    public void AcknowledgeQueuedMarker(string server, string key, string id)
    {
        var localKey = server + "/local-" + key;
        if (!savedMarkers.TryGetValue(localKey, out var anchor)) return;
        savedMarkers.Remove(localKey); markerStatuses.Remove(localKey);
        var remoteKey = server + "/" + id;
        if (savedMarkers.ContainsKey(remoteKey)) { if (anchor != null) Destroy(anchor.gameObject); return; }
        savedMarkers[remoteKey] = anchor;
        UpdateMarkerStatus(server, id, markerStatuses.TryGetValue(remoteKey, out var status) ? status : "open");
    }

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

    public void UpdateMarkerStatus(string server, string id, string value)
    {
        var key = server + "/" + id;
        markerStatuses[key] = value;
        if (!savedMarkers.TryGetValue(key, out var anchor) || anchor == null) return;
        var color = value == "resolved" ? new Color(0.1f, 0.9f, 0.3f) :
            value == "in_progress" ? new Color(0.15f, 0.5f, 1f) : new Color(1f, 0.4f, 0.05f);
        SetMarkerColor(anchor.GetComponentInChildren<Renderer>(), color);
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
        safeUi = safe;
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

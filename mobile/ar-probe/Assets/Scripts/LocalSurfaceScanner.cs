using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public sealed class LocalSurfaceScanner : MonoBehaviour
{
    private ArProbe owner;
    private ARCameraManager cameraManager;
    private AROcclusionManager depth;
    private Camera arCamera;
    private GameObject controls, previewPanel, surface, previewObject;
    private RectTransform scanFrame;
    private Text message;
    private Button scanButton, previewButton, sizeButton, saveButton;
    private ARAnchor anchor;
    private Mesh mesh;
    private MeshCollider meshCollider;
    private Material material;
    private TsdfVolume data;
    private SurfaceMeshData geometry;
    private Task<SurfaceMeshData> fusionTask;
    private TsdfVolume taskVolume;
    private bool meshRequested;
    private float nextExtraction;
    private GameObject libraryPanel;
    private Transform libraryRows;
    private Mesh loadedPreviewMesh;
    private bool returnToLibrary;
    private bool previewAfterFusion;
    private Camera previewCamera;
    private RenderTexture previewTexture;
    private bool scanning, picking, previewing;
    private bool visible;
    private float nextSample;
    private double lastTimestamp = -1, cameraTimestamp;
    private Matrix4x4 cameraPose;
    private int sizeIndex;
    private Vector3 lastCapturePosition;
    private Quaternion lastCaptureRotation;
    private float lastIntegrationTime = -100;
    private readonly float[] sizes = { 0.5f, 0.3f, 1f };
    public bool RequiresDepth => scanning || picking;
    public bool BlocksPlacement => scanning || picking || previewing || (libraryPanel != null && libraryPanel.activeSelf);

    public void Initialize(ArProbe probe, Transform ui, Camera camera, AROcclusionManager occlusion)
    {
        owner = probe; arCamera = camera; depth = occlusion;
        cameraManager = camera.GetComponent<ARCameraManager>();
        cameraManager.frameReceived += CameraFrame;
        // Preview renders only layer 30; it must never appear in the live camera or snapshots.
        arCamera.cullingMask &= ~(1 << 30);
        scanFrame = new GameObject("物品扫描框", typeof(RectTransform)).GetComponent<RectTransform>();
        scanFrame.SetParent(ui, false);
        scanFrame.anchorMin = new Vector2(0.25f, 0.57f); scanFrame.anchorMax = new Vector2(0.75f, 0.57f);
        scanFrame.offsetMin = scanFrame.offsetMax = Vector2.zero;
        var frameAspect=scanFrame.gameObject.AddComponent<AspectRatioFitter>();
        frameAspect.aspectMode=AspectRatioFitter.AspectMode.WidthControlsHeight; frameAspect.aspectRatio=1;
        Border(scanFrame, "上边", new Vector2(0, 1), Vector2.one, new Vector2(0, 4));
        Border(scanFrame, "下边", Vector2.zero, new Vector2(1, 0), new Vector2(0, 4));
        Border(scanFrame, "左边", Vector2.zero, new Vector2(0, 1), new Vector2(4, 0));
        Border(scanFrame, "右边", new Vector2(1, 0), Vector2.one, new Vector2(4, 0));
        var frameLabel = IssueReportForm.Label(scanFrame, 24);
        frameLabel.text = "仅扫描框内 · 将目标放入框中";
        frameLabel.rectTransform.anchorMin = new Vector2(0, 1); frameLabel.rectTransform.anchorMax = Vector2.one;
        frameLabel.rectTransform.pivot = new Vector2(0.5f, 0);
        frameLabel.rectTransform.sizeDelta = new Vector2(0, 45); frameLabel.rectTransform.anchoredPosition = new Vector2(0, 8);
        scanFrame.gameObject.SetActive(false);
        controls = Panel(ui, "局部表面扫描", 320, 0);
        var controlsRect = controls.GetComponent<RectTransform>();
        controlsRect.pivot = new Vector2(0.5f, 0);
        controlsRect.anchoredPosition = new Vector2(0, 170);
        message = IssueReportForm.Label(controls.transform, 25);
        message.rectTransform.anchorMin = new Vector2(0, 0.80f);
        message.text = "距离至少 0.5 米，将目标放入框内后开始。";
        scanButton = IssueReportForm.ButtonAt(controls.transform, "开始扫描", 185, 0.03f, 0.48f, ToggleScan);
        sizeButton = IssueReportForm.ButtonAt(controls.transform, "范围：0.5 米", 185, 0.52f, 0.97f, () => {
            if (data != null || RequiresDepth) return;
            sizeIndex = (sizeIndex + 1) % sizes.Length;
            sizeButton.GetComponentInChildren<Text>().text = "范围：" + sizes[sizeIndex] + " 米";
        });
        previewButton = IssueReportForm.ButtonAt(controls.transform, "完成 / 旋转查看", 105, 0.03f, 0.48f, OpenPreview);
        saveButton = IssueReportForm.ButtonAt(controls.transform, "保存模型", 105, 0.52f, 0.97f, SaveModel);
        IssueReportForm.ButtonAt(controls.transform, "本地模型", 35, 0.03f, 0.48f, ShowLibrary);
        IssueReportForm.ButtonAt(controls.transform, "清除扫描", 35, 0.52f, 0.97f, Clear);
        previewPanel = Panel(ui, "三维表面预览", 1500, 0.5f);
        var image = new GameObject("拖动旋转表面", typeof(RectTransform), typeof(RawImage), typeof(SurfacePreviewDrag));
        image.transform.SetParent(previewPanel.transform, false);
        var rect = image.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.02f, 0.14f); rect.anchorMax = new Vector2(0.98f, 0.98f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        image.GetComponent<SurfacePreviewDrag>().Rotate = delta => {
            if (previewObject == null) return;
            previewObject.transform.Rotate(Vector3.up, -delta.x * 0.3f, Space.World);
            previewObject.transform.Rotate(Vector3.right, delta.y * 0.3f, Space.World);
        };
        IssueReportForm.ButtonAt(previewPanel.transform, "返回现场 / 继续标记", 35, 0.03f, 0.97f, ClosePreview);
        previewTexture = new RenderTexture(768, 768, 16);
        image.GetComponent<RawImage>().texture = previewTexture;
        image.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        image.GetComponent<AspectRatioFitter>().aspectRatio = 1;
        var cameraObject = new GameObject("Surface preview camera");
        previewCamera = cameraObject.AddComponent<Camera>();
        previewCamera.cullingMask = 1 << 30; previewCamera.targetTexture = previewTexture;
        previewCamera.clearFlags = CameraClearFlags.SolidColor; previewCamera.backgroundColor = new Color(0.03f, 0.05f, 0.08f);
        previewCamera.enabled = false; previewCamera.nearClipPlane = 0.01f;
        // Reuse a serialized shader reference so Android shader stripping cannot remove it.
        material = new Material(owner.planeMaterial); material.color = new Color(1, 1, 1, 0.85f);
        CreateLibrary(ui);
        controls.SetActive(false); previewPanel.SetActive(false);
    }

    private static GameObject Panel(Transform parent, string name, float height, float y)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.94f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0, y); rect.anchorMax = new Vector2(1, y);
        rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(0, height);
        return panel;
    }
    private static void Border(Transform parent, string name, Vector2 min, Vector2 max, Vector2 size)
    {
        var line = new GameObject(name, typeof(RectTransform), typeof(Image)); line.transform.SetParent(parent, false);
        var image = line.GetComponent<Image>(); image.color = new Color(0.3f, 1f, 0.85f, 0.9f); image.raycastTarget = false;
        var rect = line.GetComponent<RectTransform>(); rect.anchorMin = min; rect.anchorMax = max;
        rect.sizeDelta = size; rect.anchoredPosition = Vector2.zero;
    }
    public bool IsInsideScanFrame(Vector2 point) => scanFrame != null && RectTransformUtility.RectangleContainsScreenPoint(scanFrame, point);
    private void CameraFrame(ARCameraFrameEventArgs args)
    {
        cameraTimestamp = args.timestampNs.HasValue ? args.timestampNs.Value * 1e-9 : 0;
    }
    private void ToggleScan()
    {
        if (scanning || picking) { Pause("扫描已暂停，模型已隐藏；点击实物位置放标记，或继续补扫。"); return; }
        picking = data == null; scanning = !picking;
        owner.RefreshDepth();
        scanButton.GetComponentInChildren<Text>().text = "暂停扫描";
        message.text = picking ? "请点击目标中心；仅采集该位置周围的立方范围。" : "正在补扫，请缓慢移动手机。";
    }
    public void Pick(Vector2 screenPoint)
    {
        if (!picking) return;
        if (!IsInsideScanFrame(screenPoint)) { message.text = "请点击扫描框内的目标中心；框外不会采集。"; return; }
        if (!owner.TryDepthPose(screenPoint, out Pose pose, out string reason)) { message.text = reason; return; }
        var root = new GameObject("Local surface anchor"); root.transform.SetPositionAndRotation(pose.position, Quaternion.identity);
        anchor = root.AddComponent<ARAnchor>();
        if (anchor.trackableId == TrackableId.invalidId || anchor.trackingState != TrackingState.Tracking)
        { Destroy(root); anchor = null; message.text = "目标定位未稳定，请重新选中心。"; return; }
        data = new TsdfVolume(sizes[sizeIndex]); geometry = null;
        mesh = new Mesh { name = "Observed local surface" }; mesh.MarkDynamic();
        surface = new GameObject("Observed surface", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
        surface.transform.SetParent(root.transform, false);
        surface.GetComponent<MeshFilter>().sharedMesh = mesh;
        surface.GetComponent<MeshRenderer>().sharedMaterial = material;
        meshCollider = surface.GetComponent<MeshCollider>();
        picking = false; scanning = true; lastTimestamp = -1;
        sizeButton.interactable = false;
        message.text = "正在扫描，请绕目标缓慢移动；范围外和未观察区域不补面。";
    }
    public void SetVisible(bool visible)
    {
        if (controls == null || this.visible == visible) return;
        this.visible = visible;
        controls.SetActive(visible && !previewing);
        scanFrame.gameObject.SetActive(visible && !previewing);
        if (surface != null) surface.SetActive(visible && !previewing);
        if (!visible) { previewAfterFusion = false; returnToLibrary = false; Pause(null); ClosePreview(); libraryPanel.SetActive(false); }
    }
    private void Pause(string text)
    {
        bool wasScanning=scanning;
        if (surface != null) surface.GetComponent<Renderer>().enabled=false;
        scanning = picking = false; owner.RefreshDepth();
        scanButton.GetComponentInChildren<Text>().text = data == null ? "开始扫描" : "继续补扫";
        meshRequested = true;
        if (meshCollider != null) { meshCollider.sharedMesh = null; if (mesh != null && mesh.vertexCount > 0) meshCollider.sharedMesh = mesh; }
        if (wasScanning && data != null && fusionTask == null)
        {
            taskVolume=data; var volume=data; fusionTask=Task.Run(() => volume.Extract());
        }
        if (text != null) message.text = text;
    }
    private void Update()
    {
        if (controls == null) return;
        bool ready = geometry != null && geometry.Triangles.Count > 0;
        previewButton.interactable = ready && fusionTask == null;
        saveButton.interactable = ready && !RequiresDepth && fusionTask == null;
        if (fusionTask != null && fusionTask.IsCompleted)
        {
            var completed = fusionTask; var completedVolume = taskVolume; fusionTask = null;
            if (completed.IsFaulted)
            {
                Debug.LogException(completed.Exception); // Observe failures even when this volume was cleared.
                if (data == completedVolume)
                {
                    scanning = picking = previewAfterFusion = false; owner.RefreshDepth();
                    scanButton.GetComponentInChildren<Text>().text = "继续补扫";
                    message.text = "重建未完成，请清除后重试。";
                }
            }
            else if (data == completedVolume)
            {
                if (completed.Result != null)
                {
                    geometry = completed.Result; geometry.WriteTo(mesh);
                    if (geometry.Truncated)
                    {
                        scanning = picking = false; owner.RefreshDepth();
                        scanButton.GetComponentInChildren<Text>().text = "继续补扫";
                    }
                    if (!scanning && meshCollider != null) { meshCollider.sharedMesh = null; if (geometry.Triangles.Count > 0) meshCollider.sharedMesh = mesh; }
                    Debug.Log("ARWORK_FUSION frames="+data.Frames+" vertices="+geometry.Vertices.Count+" triangles="+geometry.Triangles.Count/3
                        +" supportedPixels="+data.LastSupportedPixels+" alignmentMm="+(data.LastAlignmentMetres*1000).ToString("F1")+" alignmentSupport="+data.LastAlignmentSupport+" integrationMs="+data.LastIntegrationMilliseconds.ToString("F1"));
                    if (previewAfterFusion && visible && !libraryPanel.activeSelf && geometry.Triangles.Count>0) { previewAfterFusion=false; ShowPreview(mesh); }
                }
                else if (!scanning)
                {
                    taskVolume=data; var volume=data; fusionTask=Task.Run(() => volume.Extract());
                }
                message.text = "已融合 " + data.Frames + " 帧，请缓慢绕目标补充侧面和顶部。";
                if (data.Frames < 3 || geometry == null || geometry.Triangles.Count == 0)
                    message.text = "请缓慢绕目标移动，等待多帧一致的表面。已融合 " + data.Frames + " 帧";
                if (geometry != null && geometry.Truncated) message.text = "网格达到容量上限，当前仅为部分表面。请保存或清除后缩小范围重扫。";
            }
        }
        if (surface != null) surface.GetComponent<Renderer>().enabled = scanning && visible && !previewing && ARSession.state == ARSessionState.SessionTracking
            && anchor != null && anchor.trackingState == TrackingState.Tracking;
        if (!scanning || fusionTask != null || Time.unscaledTime < nextSample) return;
        nextSample = Time.unscaledTime + 0.4f;
        if (ARSession.state != ARSessionState.SessionTracking || anchor == null || anchor.trackingState != TrackingState.Tracking)
        { Pause("跟踪丢失，扫描已暂停；恢复定位后再继续。"); return; }
        if (Time.unscaledTime-lastIntegrationTime < 1f
            && Vector3.Distance(arCamera.transform.position, lastCapturePosition) < 0.015f
            && Quaternion.Angle(arCamera.transform.rotation, lastCaptureRotation) < 4) return;
        if (!cameraManager.TryGetIntrinsics(out var intrinsics) || intrinsics.focalLength.x <= 0 || intrinsics.focalLength.y <= 0
            || !depth.TryAcquireSmoothedEnvironmentDepthCpuImage(out var image))
        { message.text = "等待可用深度数据，请缓慢移动手机。"; return; }
        using (image)
        {
            if (image.timestamp <= lastTimestamp || cameraTimestamp == 0 || Math.Abs(cameraTimestamp-image.timestamp) > 0.10)
            { message.text = "等待与相机同步的新深度帧。"; return; }
            if (image.format != XRCpuImage.Format.DepthUint16) return;
            lastTimestamp = image.timestamp;
            var frame = ReadFrame(image, intrinsics);
            bool extract = Time.unscaledTime >= nextExtraction || meshRequested;
            if (extract) { nextExtraction = Time.unscaledTime+1.2f; meshRequested = false; }
            taskVolume = data; var volume = data;
            fusionTask = Task.Run(() => { volume.Integrate(frame); return extract ? volume.Extract() : null; });
        }
        lastIntegrationTime = Time.unscaledTime;
        lastCapturePosition = arCamera.transform.position; lastCaptureRotation = arCamera.transform.rotation;
        message.text = "正在融合表面，请绕目标缓慢移动。";
    }
    private TsdfVolume.Frame ReadFrame(XRCpuImage image, XRCameraIntrinsics intrinsics)
    {
        // Read after TrackedPoseDriver.Update, rather than caching the previous pose in the camera callback.
        cameraPose=SurfaceMeshData.NativeCameraToWorld(arCamera.transform.localToWorldMatrix,Screen.orientation);
        int width=image.width, height=image.height;
        float calibrationScale = Mathf.Max((float)width/intrinsics.resolution.x, (float)height/intrinsics.resolution.y);
        var frame = new TsdfVolume.Frame { Width=width, Height=height, Depth=new float[width*height], Mask=new bool[width*height],
            Confidence=new byte[width*height], Focal=intrinsics.focalLength*calibrationScale,
            Principal=(intrinsics.principalPoint-(Vector2)intrinsics.resolution*0.5f)*calibrationScale
                +new Vector2(width,height)*0.5f-Vector2.one*0.5f,
            VolumeToCamera=cameraPose.inverse*anchor.transform.localToWorldMatrix };
        var plane=image.GetPlane(0);
        for (int y=0;y<height;y++) for (int x=0;x<width;x++)
        {
            int offset=y*plane.rowStride+x*plane.pixelStride, index=y*width+x;
            float z=(plane.data[offset]|plane.data[offset+1]<<8)*0.001f;
            frame.Depth[index]=z;
            if (z<0.5f||z>3f) continue;
            var p=SurfaceMeshData.Unproject(x,y,z,width,height,intrinsics.focalLength,intrinsics.principalPoint,intrinsics.resolution);
            var screen=arCamera.WorldToScreenPoint(cameraPose.MultiplyPoint3x4(p));
            frame.Mask[index]=screen.z>0 && IsInsideScanFrame(new Vector2(screen.x,screen.y));
        }
        // Raw depth between source updates can be reprojected by ARCore to the current pose.
        // Its source timestamp is not a reason to discard otherwise matching spatial evidence.
        if (depth.TryAcquireRawEnvironmentDepthCpuImage(out var raw))
        {
            using (raw)
            {
                if (depth.TryAcquireEnvironmentDepthConfidenceCpuImage(out var confidence))
                using (confidence)
                {
                    if (raw.width==width && raw.height==height && confidence.width==width && confidence.height==height
                        && raw.format==XRCpuImage.Format.DepthUint16 && Math.Abs(raw.timestamp-confidence.timestamp)<0.02)
                    {
                        var rp=raw.GetPlane(0); var cp=confidence.GetPlane(0);
                        for (int y=0;y<height;y++) for (int x=0;x<width;x++)
                        {
                            int index=y*width+x, offset=y*rp.rowStride+x*rp.pixelStride;
                            byte certainty=cp.data[y*cp.rowStride+x*cp.pixelStride];
                            float measured=(rp.data[offset]|rp.data[offset+1]<<8)*0.001f;
                            if (certainty<128 || measured<0.5f || measured>3f) continue;
                            if (Mathf.Abs(measured-frame.Depth[index])>Mathf.Max(0.03f,measured*0.03f)) frame.Mask[index]=false;
                            else { frame.Depth[index]=measured; frame.Confidence[index]=certainty; }
                        }
                    }
                }
            }
        }
        ReadColor(frame);
        return frame;
    }
    private void ReadColor(TsdfVolume.Frame frame)
    {
        if (!cameraManager.TryAcquireLatestCpuImage(out var image)) return;
        using (image)
        {
            if (cameraTimestamp>0 && Math.Abs(image.timestamp-cameraTimestamp)>0.1) return;
            if (!image.FormatSupported(TextureFormat.RGB24)) return;
            int width=Mathf.Min(512,image.width), height=Mathf.RoundToInt((float)image.height*width/image.width);
            var parameters=new XRCpuImage.ConversionParams { inputRect=new RectInt(0,0,image.width,image.height),
                outputDimensions=new Vector2Int(width,height), outputFormat=TextureFormat.RGB24, transformation=XRCpuImage.Transformation.None };
            var bytes=new byte[image.GetConvertedDataSize(parameters)]; var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try { image.Convert(parameters,pin.AddrOfPinnedObject(),bytes.Length); }
            finally { pin.Free(); }
            frame.Rgb=bytes; frame.ColorWidth=width; frame.ColorHeight=height;
        }
    }
    public IssueScanModel FreezeIssue(Vector3 point, Vector3 normal)
    {
        if (scanning || fusionTask != null || anchor == null || anchor.trackingState != TrackingState.Tracking)
            throw new InvalidOperationException("请暂停扫描并等待表面完成后重新选点。");
        return IssueScanModel.Freeze(geometry, surface.transform.worldToLocalMatrix, point, normal);
    }

    public bool Raycast(Vector2 position, out Pose pose)
    {
        pose = default;
        if (scanning || previewing || meshCollider == null || !surface.activeInHierarchy || anchor.trackingState != TrackingState.Tracking) return false;
        if (!meshCollider.Raycast(arCamera.ScreenPointToRay(position), out var hit, 5f)) return false;
        var normal = Vector3.Dot(hit.normal, arCamera.transform.position-hit.point) >= 0 ? hit.normal : -hit.normal;
        pose = new Pose(hit.point, Quaternion.FromToRotation(Vector3.up, normal)); return true;
    }
    private void OpenPreview()
    {
        if (data == null || (geometry == null && fusionTask == null)) return;
        Pause(null);
        if (fusionTask != null) { previewAfterFusion=true; message.text="正在完成表面，请稍候…"; }
        else if (geometry != null && geometry.Triangles.Count > 0) ShowPreview(mesh);
    }
    private void ShowPreview(Mesh model)
    {
        previewing = true; controls.SetActive(false); if (surface != null) surface.SetActive(false); scanFrame.gameObject.SetActive(false);
        previewPanel.SetActive(true); previewPanel.transform.SetAsLastSibling();
        previewObject = new GameObject("Frozen surface preview", typeof(MeshFilter), typeof(MeshRenderer));
        previewObject.layer = 30;
        previewObject.GetComponent<MeshFilter>().sharedMesh = model;
        previewObject.GetComponent<MeshRenderer>().sharedMaterial = material;
        var bounds = model.bounds;
        previewObject.transform.position = new Vector3(0, 1000, 0) - bounds.center;
        previewCamera.transform.SetPositionAndRotation(new Vector3(0, 1000, -Mathf.Max(0.3f, bounds.size.magnitude * 1.3f)), Quaternion.identity);
        previewCamera.farClipPlane = Mathf.Max(10, bounds.size.magnitude * 4);
        // Rotate about the scanned bounds, rather than the selected region's corner.
        var pivot = new GameObject("Preview pivot"); pivot.transform.position = new Vector3(0, 1000, 0);
        previewObject.transform.SetParent(pivot.transform, true); previewObject = pivot;
        previewCamera.enabled = true;
    }
    private void ClosePreview()
    {
        previewing = false;
        if (previewPanel != null) previewPanel.SetActive(false);
        if (previewCamera != null) previewCamera.enabled = false;
        if (previewObject != null) Destroy(previewObject);
        if (loadedPreviewMesh != null) { Destroy(loadedPreviewMesh); loadedPreviewMesh=null; }
        if (controls != null) controls.SetActive(visible);
        if (scanFrame != null) scanFrame.gameObject.SetActive(visible);
        if (surface != null) surface.SetActive(visible);
        if (returnToLibrary) { returnToLibrary=false; ShowLibrary(); }
    }
    private void Clear()
    {
        previewAfterFusion=false; returnToLibrary=false;
        Pause(null); ClosePreview();
        if (surface != null)
        {
            surface.SetActive(false);
            surface.GetComponent<MeshFilter>().sharedMesh = null;
        }
        if (meshCollider != null) { meshCollider.enabled = false; meshCollider.sharedMesh = null; }
        if (anchor != null) Destroy(anchor.gameObject);
        if (mesh != null) Destroy(mesh);
        surface = null; anchor = null; meshCollider = null; mesh = null; data = null; geometry = null;
        lastTimestamp = -1; nextSample = 0; lastIntegrationTime = -100;
        sizeButton.interactable = true; previewButton.interactable = false; saveButton.interactable=false;
        scanButton.GetComponentInChildren<Text>().text = "开始扫描";
        message.text = "已清除全部扫描表面，请重新选择目标中心。";
        Debug.Log("ARWORK_SURFACE_CLEARED vertices=0 triangles=0");
    }
    private void SaveModel()
    {
        if (geometry == null || data == null || RequiresDepth || fusionTask != null) return;
        try
        {
            string path=SurfaceScanStore.Save(SurfaceScanStore.DirectoryPath,geometry,data.Side,data.Frames);
            message.text="模型已离线保存，可在“本地模型”中查看。";
            Debug.Log("ARWORK_SURFACE_SAVED file="+Path.GetFileName(path)+" vertices="+geometry.Vertices.Count+" frames="+data.Frames);
        }
        catch (Exception error) { message.text="保存失败，当前模型保留，请重试。"; Debug.LogException(error); }
    }
    private void CreateLibrary(Transform ui)
    {
        libraryPanel=new GameObject("本地扫描模型",typeof(RectTransform),typeof(Image)); libraryPanel.transform.SetParent(ui,false);
        var rect=libraryPanel.GetComponent<RectTransform>(); rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.offsetMin=rect.offsetMax=Vector2.zero;
        libraryPanel.GetComponent<Image>().color=new Color(0.02f,0.05f,0.08f,1);
        var title=IssueReportForm.Label(libraryPanel.transform,30); title.text="本地模型 · 保存在这台手机，无需联网";
        title.rectTransform.anchorMin=new Vector2(0.03f,0.91f); title.rectTransform.anchorMax=new Vector2(0.97f,0.97f);
        var scrollObject=new GameObject("模型列表",typeof(RectTransform),typeof(Image),typeof(Mask),typeof(ScrollRect)); scrollObject.transform.SetParent(libraryPanel.transform,false);
        var scrollRect=scrollObject.GetComponent<RectTransform>(); scrollRect.anchorMin=new Vector2(0.03f,0.12f); scrollRect.anchorMax=new Vector2(0.97f,0.89f); scrollRect.offsetMin=scrollRect.offsetMax=Vector2.zero;
        scrollObject.GetComponent<Image>().color=new Color(0.04f,0.08f,0.1f); scrollObject.GetComponent<Mask>().showMaskGraphic=true;
        var rows=new GameObject("模型条目",typeof(RectTransform),typeof(VerticalLayoutGroup),typeof(ContentSizeFitter)); rows.transform.SetParent(scrollObject.transform,false);
        var content=rows.GetComponent<RectTransform>(); content.anchorMin=new Vector2(0,1); content.anchorMax=Vector2.one; content.pivot=new Vector2(0.5f,1); content.sizeDelta=Vector2.zero;
        var layout=rows.GetComponent<VerticalLayoutGroup>(); layout.spacing=12; layout.childControlHeight=true; layout.childForceExpandHeight=false;
        rows.GetComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;
        var scroll=scrollObject.GetComponent<ScrollRect>(); scroll.content=content; scroll.viewport=scrollRect; scroll.horizontal=false;
        libraryRows=rows.transform;
        IssueReportForm.ButtonAt(libraryPanel.transform,"返回",55,0.03f,0.97f,() => {
            libraryPanel.SetActive(false); controls.SetActive(visible); scanFrame.gameObject.SetActive(visible);
            if (surface != null) surface.SetActive(visible);
        });
        libraryPanel.SetActive(false);
    }
    public void ShowLibrary()
    {
        previewAfterFusion=false;
        Pause(null); controls.SetActive(false); scanFrame.gameObject.SetActive(false); if (surface != null) surface.SetActive(false);
        libraryPanel.SetActive(true); libraryPanel.transform.SetAsLastSibling();
        foreach (Transform row in libraryRows) { row.gameObject.SetActive(false); Destroy(row.gameObject); }
        try
        {
            var paths=Directory.Exists(SurfaceScanStore.DirectoryPath) ? Directory.GetFiles(SurfaceScanStore.DirectoryPath,"*.arscan") : Array.Empty<string>();
            Array.Sort(paths,StringComparer.Ordinal); Array.Reverse(paths);
            if (paths.Length==0)
            {
                var empty=new GameObject("暂无已保存模型",typeof(RectTransform),typeof(LayoutElement)); empty.transform.SetParent(libraryRows,false);
                empty.GetComponent<LayoutElement>().preferredHeight=100; IssueReportForm.Label(empty.transform,28).text="暂无模型，扫描并暂停后可离线保存。";
            }
            foreach (string path in paths)
            {
                var row=new GameObject("已保存模型",typeof(RectTransform),typeof(Image),typeof(Button),typeof(LayoutElement)); row.transform.SetParent(libraryRows,false);
                row.GetComponent<Image>().color=new Color(0.08f,0.25f,0.28f); row.GetComponent<LayoutElement>().preferredHeight=100;
                IssueReportForm.Label(row.transform,27).text=Path.GetFileNameWithoutExtension(path)+"\n点击旋转查看 · OBJ 已同时保存";
                row.GetComponent<Button>().onClick.AddListener(() => {
                    try
                    {
                        var saved=SurfaceScanStore.Load(path); loadedPreviewMesh=new Mesh(); saved.Geometry.WriteTo(loadedPreviewMesh);
                        libraryPanel.SetActive(false); returnToLibrary=true; ShowPreview(loadedPreviewMesh);
                    }
                    catch (Exception error) { Debug.LogException(error); IssueReportForm.Label(row.transform,24).text="文件读取失败，原文件保留。"; }
                });
            }
        }
        catch (Exception error) { Debug.LogException(error); }
    }
    private void OnDestroy()
    {
        if (cameraManager != null) cameraManager.frameReceived -= CameraFrame;
        if (anchor != null) Destroy(anchor.gameObject);
        if (mesh != null) Destroy(mesh);
        if (loadedPreviewMesh != null) Destroy(loadedPreviewMesh);
        if (material != null) Destroy(material);
        if (previewObject != null) Destroy(previewObject);
        if (previewCamera != null) Destroy(previewCamera.gameObject);
        if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
    }
}

public sealed class SurfacePreviewDrag : MonoBehaviour, IDragHandler
{
    public Action<Vector2> Rotate;
    public void OnDrag(PointerEventData data) { Rotate?.Invoke(data.delta); }
}

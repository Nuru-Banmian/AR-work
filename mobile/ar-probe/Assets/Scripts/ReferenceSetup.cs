using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// Enrollment is an explicit camera flow; browsing this page never opens the camera.
public sealed class ReferenceSetup : MonoBehaviour
{
    [Serializable] private sealed class Registration {
        public string id, name, location, imageBase64;
        public int version;
        public float widthMeters, heightMeters;
    }
    [Serializable] private sealed class Pending { public string server, body; }
    private string PendingPath => Path.Combine(Application.persistentDataPath, "reference-registration.json");
    private ReferenceCatalog catalog;
    private ArProbe probe;
    private Canvas canvas;
    private GameObject panel, cameraControls;
    private RectTransform guide;
    private RawImage preview;
    private Text message, selectedLabel, photoHint;
    private InputField targetName, location, width, height;
    private Button save, photo, refresh, cycle, back;
    private Texture2D draftImage, displayedImage;
    private string frozenBody, frozenServer, draftServer, encodedImage;
    private ReferenceTarget replacing;
    private bool busy, imageVerified, pendingInvalid;
    private Action closed;
    public bool IsOpen => panel != null && (panel.activeSelf || cameraControls.activeSelf);

    public void Initialize(Transform parent, ReferenceCatalog source, ArProbe owner)
    {
        catalog = source; probe = owner; canvas = parent.GetComponentInParent<Canvas>();
        panel = new GameObject("现场参照登记", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 1);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        selectedLabel = IssueReportForm.Label(rect, 26);
        selectedLabel.rectTransform.anchorMin = new Vector2(0.03f, 1); selectedLabel.rectTransform.anchorMax = new Vector2(0.97f, 1);
        selectedLabel.rectTransform.pivot = new Vector2(0.5f, 1); selectedLabel.rectTransform.sizeDelta = new Vector2(0, 120);
        selectedLabel.rectTransform.anchoredPosition = new Vector2(0, -10);
        cycle = IssueReportForm.ButtonAt(rect, "切换当前参照", 1230, 0.03f, 0.48f, Cycle);
        IssueReportForm.ButtonAt(rect, "登记新参照", 1230, 0.52f, 0.97f, () => { if (!busy && frozenBody == null) NewDraft(null); });
        preview = new GameObject("参照图案预览", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        preview.transform.SetParent(rect, false);
        preview.rectTransform.anchorMin = new Vector2(0.05f, 0); preview.rectTransform.anchorMax = new Vector2(0.95f, 0);
        preview.rectTransform.pivot = new Vector2(0.5f, 0); preview.rectTransform.sizeDelta = new Vector2(0, 350); preview.rectTransform.anchoredPosition = new Vector2(0, 860);
        preview.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        // A separate preview container prevents its fitter from covering the whole page.
        var previewBox = new GameObject("参照预览区域", typeof(RectTransform)).GetComponent<RectTransform>();
        previewBox.SetParent(rect, false); previewBox.anchorMin = new Vector2(0.05f, 0); previewBox.anchorMax = new Vector2(0.95f, 0);
        previewBox.pivot = new Vector2(0.5f, 0); previewBox.sizeDelta = new Vector2(0, 350); previewBox.anchoredPosition = new Vector2(0, 860);
        preview.transform.SetParent(previewBox, false);
        IssueReportForm.ButtonAt(rect, "参照已移动/更换：登记下一版本", 780, 0.03f, 0.97f, () => {
            if (!busy && frozenBody == null && catalog.Selected != null) NewDraft(catalog.Selected);
        });
        targetName = IssueReportForm.Field(rect, "参照名称，例如东门标牌", 690, 70, false);
        location = IssueReportForm.Field(rect, "位置说明，例如东门内侧墙面", 600, 70, false);
        width = IssueReportForm.Field(rect, "实测图案宽度（厘米）", 510, 70, false);
        height = IssueReportForm.Field(rect, "实测图案高度（厘米）", 420, 70, false);
        foreach (var field in new[] { targetName, location, width, height }) field.onValueChanged.AddListener(_ => {
            if (draftServer == null) draftServer = catalog.Server;
        });
        photoHint = IssueReportForm.Label(rect, 20);
        photoHint.gameObject.name = "拍摄尺寸提示";
        photoHint.rectTransform.anchorMin = new Vector2(0.03f, 0); photoHint.rectTransform.anchorMax = new Vector2(0.97f, 0);
        photoHint.rectTransform.pivot = new Vector2(0.5f, 0); photoHint.rectTransform.sizeDelta = new Vector2(0, 30); photoHint.rectTransform.anchoredPosition = new Vector2(0, 390);
        photo = IssueReportForm.ButtonAt(rect, "拍摄 / 重拍图案", 330, 0.03f, 0.97f, BeginPhoto);
        save = IssueReportForm.ButtonAt(rect, "确认并保存参照", 240, 0.03f, 0.97f, () => { if (!busy) StartCoroutine(Save()); });
        refresh = IssueReportForm.ButtonAt(rect, "下载 / 刷新参照", 150, 0.03f, 0.97f, () => { if (!busy) StartCoroutine(Refresh()); });
        back = IssueReportForm.ButtonAt(rect, "返回设置", 60, 0.03f, 0.97f, () => { if (!busy) { panel.SetActive(false); closed?.Invoke(); } });
        message = IssueReportForm.Label(rect, 24);
        message.rectTransform.anchorMin = new Vector2(0.03f, 1); message.rectTransform.anchorMax = new Vector2(0.97f, 1);
        message.rectTransform.pivot = new Vector2(0.5f, 1); message.rectTransform.sizeDelta = new Vector2(0, 150); message.rectTransform.anchoredPosition = new Vector2(0, -130);
        cameraControls = new GameObject("参照拍摄", typeof(RectTransform)); cameraControls.transform.SetParent(parent, false);
        var cameraRect = cameraControls.GetComponent<RectTransform>(); cameraRect.anchorMin = Vector2.zero; cameraRect.anchorMax = Vector2.one; cameraRect.offsetMin = cameraRect.offsetMax = Vector2.zero;
        guide = new GameObject("图案边界取景框", typeof(RectTransform), typeof(Image), typeof(Outline)).GetComponent<RectTransform>();
        guide.SetParent(cameraRect, false); guide.GetComponent<Image>().color = new Color(1, 1, 1, 0.1f); guide.GetComponent<Image>().raycastTarget = false;
        guide.GetComponent<Outline>().effectColor = Color.white; guide.GetComponent<Outline>().effectDistance = new Vector2(3, 3);
        guide.anchorMin = guide.anchorMax = guide.pivot = new Vector2(0.5f, 0.5f);
        IssueReportForm.ButtonAt(cameraRect, "取消拍摄", 130, 0.03f, 0.48f, () => { if (!busy) FinishPhoto(); });
        IssueReportForm.ButtonAt(cameraRect, "拍摄框内图案", 130, 0.52f, 0.97f, () => { if (!busy) StartCoroutine(Capture()); });
        panel.SetActive(false); cameraControls.SetActive(false);
        try
        {
            if (File.Exists(PendingPath))
            {
                var pending = JsonUtility.FromJson<Pending>(File.ReadAllText(PendingPath));
                var body = JsonUtility.FromJson<Registration>(pending.body);
                if (string.IsNullOrEmpty(pending.server) || string.IsNullOrEmpty(body?.id)) throw new ArgumentException("Invalid pending enrollment");
                draftServer = frozenServer = pending.server; frozenBody = pending.body;
                targetName.text = body.name; location.text = body.location;
                width.text = (body.widthMeters*100).ToString(CultureInfo.InvariantCulture); height.text = (body.heightMeters*100).ToString(CultureInfo.InvariantCulture);
                draftImage = new Texture2D(2,2,TextureFormat.RGB24,false); draftImage.LoadImage(Convert.FromBase64String(body.imageBase64));
                preview.texture = draftImage; preview.GetComponent<AspectRatioFitter>().aspectRatio = (float)draftImage.width/draftImage.height;
            }
        }
        catch (Exception e) { message.text = "待确认登记无法读取，请保留应用数据联系维护人员。"; pendingInvalid = true; Debug.LogException(e); }
    }
    public void Open(string server, Action onClosed)
    {
        if (busy) return;
        catalog.SetServer(server);
        if (draftServer != null && draftServer != server)
        {
            closed = onClosed; panel.SetActive(true);
            message.text = "参照草稿属于 " + draftServer + "，请切回原服务继续；或点击登记新参照明确放弃当前草稿。";
            return;
        }
        if (frozenBody != null && server != frozenServer) { closed = onClosed; panel.SetActive(true); message.text = "待确认登记属于 " + frozenServer + "，请切回原服务重试。"; return; }
        catalog.SetServer(server); closed = onClosed; panel.SetActive(true);
        ShowSelected(); StartCoroutine(Refresh());
    }
    private void NewDraft(ReferenceTarget previous)
    {
        if (pendingInvalid) return;
        draftServer = catalog.Server; replacing = previous; imageVerified = false; encodedImage = null; frozenBody = frozenServer = null;
        if (draftImage != null) Destroy(draftImage); draftImage = null;
        targetName.text = previous?.name ?? ""; location.text = previous?.location ?? "";
        width.text = previous == null ? "" : (previous.widthMeters * 100).ToString(CultureInfo.InvariantCulture);
        height.text = previous == null ? "" : (previous.heightMeters * 100).ToString(CultureInfo.InvariantCulture);
        preview.texture = null;
        message.text = previous == null ? "选固定、平整、独特的图案；测量图案本身宽高，勿包含外框。" : "新版本成功保存后旧版停用，旧点位须重新采集。系统不能自动检测图案被移动。";
    }
    private void ShowSelected()
    {
        selectedLabel.text = catalog.Selected == null ? "尚未选择现场参照" : "当前：" + catalog.Selected.Caption;
        if (draftImage != null || frozenBody != null) return;
        if (displayedImage != null) Destroy(displayedImage);
        try { displayedImage = catalog.Selected?.DecodeImage(); preview.texture = displayedImage; if (displayedImage != null) preview.GetComponent<AspectRatioFitter>().aspectRatio = (float)displayedImage.width / displayedImage.height; }
        catch (Exception e) { message.text = "参照图片不可用，请刷新。"; Debug.LogException(e); }
    }
    private void Cycle()
    {
        if (busy || frozenBody != null) return;
        var active = Array.FindAll(catalog.Targets, t => t.active);
        if (active.Length == 0) return;
        int index = Array.IndexOf(active, catalog.Selected); catalog.Select(active[(index + 1) % active.Length]); ShowSelected();
    }
    private IEnumerator Refresh()
    {
        busy = true; yield return catalog.Refresh(); busy = false; ShowSelected(); message.text = catalog.Message;
    }
    private bool Dimensions(out float w, out float h, bool explain = true)
    {
        w = h = 0;
        if (!float.TryParse(width.text, NumberStyles.Float, CultureInfo.InvariantCulture, out w) ||
            !float.TryParse(height.text, NumberStyles.Float, CultureInfo.InvariantCulture, out h) ||
            float.IsNaN(w) || float.IsNaN(h) || w < 5 || h < 5 || w > 500 || h > 500 || w/h > 4 || h/w > 4)
        {
            if (explain) message.text = "拍摄前请填写图案实测宽高（厘米）：各 5–500，长宽比不超过 4。";
            return false;
        }
        w /= 100; h /= 100; return true;
    }
    private void BeginPhoto()
    {
        if (busy || (draftServer != null && draftServer != catalog.Server) || frozenBody != null || pendingInvalid) return;
        if (!Dimensions(out float w, out float h))
        {
            var missing = !float.TryParse(width.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float measuredWidth) ||
                float.IsNaN(measuredWidth) || measuredWidth < 5 || measuredWidth > 500 ? width : height;
            missing.Select(); missing.ActivateInputField();
            return;
        }
        float pixelsWide = Mathf.Min(Screen.width * 0.8f, Screen.height * 0.55f * w/h);
        guide.sizeDelta = new Vector2(pixelsWide, pixelsWide * h/w) / canvas.scaleFactor;
        panel.SetActive(false); cameraControls.SetActive(true); probe.BeginEnrollment();
    }
    private void FinishPhoto() { probe.LeaveCamera(); cameraControls.SetActive(false); panel.SetActive(true); }
    private IEnumerator Capture()
    {
        var camera = probe.SceneCamera;
        var cameraManager = camera.GetComponent<UnityEngine.XR.ARFoundation.ARCameraManager>();
        if (!probe.CameraActive || !cameraManager.permissionGranted || UnityEngine.XR.ARFoundation.ARSession.state != UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking) yield break;
        busy = true; imageVerified = false; encodedImage = null;
        var corners = new Vector3[4]; guide.GetWorldCorners(corners);
        var rect = new Rect(Mathf.Round(corners[0].x), Mathf.Round(corners[0].y), Mathf.Round(corners[2].x-corners[0].x), Mathf.Round(corners[2].y-corners[0].y));
        canvas.enabled = false; yield return new WaitForEndOfFrame();
        Texture2D cropped = null; RenderTexture scaled = null; var previous = RenderTexture.active;
        try
        {
            cropped = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGB24, false);
            cropped.ReadPixels(rect, 0, 0); cropped.Apply();
            float scale = 1024f / Mathf.Max(cropped.width, cropped.height);
            int w = Mathf.RoundToInt(cropped.width*scale), h = Mathf.RoundToInt(cropped.height*scale);
            scaled = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32); Graphics.Blit(cropped, scaled); RenderTexture.active = scaled;
            if (draftImage != null) Destroy(draftImage);
            draftImage = new Texture2D(w, h, TextureFormat.RGB24, false); draftImage.ReadPixels(new Rect(0,0,w,h),0,0); draftImage.Apply();
            var jpeg = draftImage.EncodeToJPG(85);
            if (jpeg.Length > 512*1024) jpeg = draftImage.EncodeToJPG(65);
            if (jpeg.Length > 512*1024) throw new ArgumentException("Encoded reference exceeds 512 KiB");
            // Validate the exact decoded JPEG pixels that subsequent devices will track.
            var decoded = new Texture2D(2,2,TextureFormat.RGB24,false);
            if (!decoded.LoadImage(jpeg)) { Destroy(decoded); throw new ArgumentException("JPEG could not be decoded"); }
            Destroy(draftImage); draftImage = decoded; encodedImage = Convert.ToBase64String(jpeg);
            preview.texture = draftImage; preview.GetComponent<AspectRatioFitter>().aspectRatio = (float)w/h;
            message.text = "确认图案完整、正面清晰且与实测区域一致。模糊、纯色或重复图案应重拍。";
        }
        catch (Exception e) { message.text = "拍摄失败，请重新拍摄。"; Debug.LogException(e); }
        finally { RenderTexture.active = previous; if (scaled != null) RenderTexture.ReleaseTemporary(scaled); if (cropped != null) Destroy(cropped); canvas.enabled = true; }
        if (draftImage != null && encodedImage != null && Dimensions(out float measuredWidth, out _))
        {
            yield return probe.ValidateReferenceImage(draftImage, measuredWidth, valid => imageVerified = valid);
            message.text = imageVerified ? "图案特征验证通过。请确认完整、正面清晰及实测尺寸，然后保存。" : "此图案无法可靠识别，请选择纹理丰富的固定标牌并重新拍摄。";
        }
        busy = false; FinishPhoto();
    }
    private IEnumerator Save()
    {
        if (draftServer != null && draftServer != catalog.Server) { message.text = "草稿属于原服务，请切回原服务后再保存。"; yield break; }
        if (pendingInvalid) { message.text = "待确认登记损坏，请保留应用数据联系维护人员。"; yield break; }
        if (frozenBody == null)
        {
            if (draftImage == null || !imageVerified || !Dimensions(out float w, out float h) || string.IsNullOrWhiteSpace(targetName.text) || string.IsNullOrWhiteSpace(location.text)) { message.text = "请填写名称、位置、实测尺寸并拍摄图案。"; yield break; }
            if (Mathf.Abs(((float)draftImage.width/draftImage.height)/(w/h)-1) > 0.015f) { message.text = "尺寸比例已改变，请重新拍摄。"; yield break; }
            frozenBody = JsonUtility.ToJson(new Registration { id = replacing?.id ?? Guid.NewGuid().ToString("N"), version = replacing == null ? 1 : replacing.version + 1,
                name = targetName.text.Trim(), location = location.text.Trim(), widthMeters = w, heightMeters = h, imageBase64 = encodedImage });
            frozenServer = catalog.Server;
            try
            {
                var temp = PendingPath + ".tmp"; File.WriteAllText(temp, JsonUtility.ToJson(new Pending { server = frozenServer, body = frozenBody }));
                if (File.Exists(PendingPath)) File.Replace(temp, PendingPath, null); else File.Move(temp, PendingPath);
            }
            catch (Exception e) { frozenBody = frozenServer = null; message.text = "本机保存登记失败，尚未发送，请重试。"; Debug.LogException(e); yield break; }
        }
        if (frozenServer != catalog.Server) { message.text = "请切回待确认登记所属服务后重试。"; yield break; }
        busy = true; message.text = "正在保存参照…";
        using (var request = new UnityWebRequest(frozenServer + "/api/references", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(frozenBody)); request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 20; yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                var submitted = JsonUtility.FromJson<Registration>(frozenBody);
                ClearPending();
                frozenBody = frozenServer = draftServer = null; replacing = null;
                foreach (var field in new[] { targetName, location, width, height }) field.SetTextWithoutNotify("");
                if (draftImage != null) Destroy(draftImage); draftImage = null;
                yield return catalog.Refresh();
                var target = Array.Find(catalog.Targets, t => t.id == submitted.id && t.version == submitted.version && t.active);
                if (target != null) catalog.Select(target);
                ShowSelected(); message.text = target == null ? "服务器已保存，下载未完成，请刷新参照。" : "参照已保存并选中。进入新增，扫描验证图案后才能选点。";
            }
            else if (request.responseCode == 400 || request.responseCode == 409)
            {
                ClearPending(); frozenBody = frozenServer = null;
                message.text = "登记被拒绝：请检查尺寸、图片或版本；重复图案请刷新后选择已有参照。内容已保留，可修正后再保存。";
            }
            else message.text = "参照登记未确认。请检查网络；重试会保持原请求，重启后仍可继续确认。";
        }
        busy = false;
    }
    private void ClearPending()
    {
        try { File.Delete(PendingPath); }
        catch (Exception e) { Debug.LogWarning("Pending reference receipt could not be removed; replay remains safe. " + e.Message); }
    }
    private void Update()
    {
        bool hasDimensions = Dimensions(out _, out _, false);
        photo.GetComponentInChildren<Text>().text = hasDimensions ? "拍摄 / 重拍图案" : "先填写实测宽高，再拍摄";
        photoHint.text = hasDimensions ? "请正对固定图案，使四边贴合取景框。" : "必填：图案实测宽高（厘米），各 5–500，长宽比 ≤ 4。";
        photoHint.color = hasDimensions ? new Color(0.7f, 1f, 0.7f) : new Color(1f, 0.75f, 0.35f);
        bool editable = !busy && !pendingInvalid && frozenBody == null && (draftServer == null || draftServer == catalog.Server);
        targetName.interactable = location.interactable = width.interactable = height.interactable = photo.interactable = cycle.interactable = editable;
        refresh.interactable = back.interactable = !busy;
        save.interactable = !busy && !pendingInvalid && (draftServer == null || draftServer == catalog.Server) && (frozenBody == null || frozenServer == catalog.Server);
        save.GetComponentInChildren<Text>().text = frozenBody == null ? "确认并保存参照" : "重试确认原登记";
    }
    private void OnDestroy() { if (draftImage != null) Destroy(draftImage); if (displayedImage != null) Destroy(displayedImage); }
}

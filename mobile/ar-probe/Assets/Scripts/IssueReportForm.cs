using System;
using System.Collections;
using System.Net.Http;
using UnityEngine;
using UnityEngine.UI;

public sealed class IssueReportForm : MonoBehaviour
{
    [Serializable] private sealed class PendingRequest { public string key, body, server, endpoint; }
    private const string PendingPreference = "arwork.pending-report";

    private GameObject reportPanel, homeControls, settingsPanel, captureControls, previewPanel;
    private Canvas canvas;
    private RawImage preview;
    private Text snapshotMessage;
    private Texture2D snapshot;
    private Action snapshotConfirmed;
    private Button capture;
    private bool capturing;
    private string activeServer, draftServer, draftReceiverId;
    private Text settingsMessage;
    private string receiverId;
    private readonly HttpClient receiverClient = ReceiverConnection.CreateClient(4);
    public bool NavigationLocked => busy || capturing;
    private bool positionLocked;
    public bool PositionLocked { get => capturing || (history != null && history.IsRemarking ? history.RemarkLocked : positionLocked || !probe.CameraActive || (history != null && history.IsOpen)); private set => positionLocked = value; }
    private IssueHistoryPanel history;
    private IssueLiveUpdates live;
    private LocalReports local;
    private ArProbe probe;
    private InputField address, title, description;
    private Text message, priorityLabel, editLabel;
    private Button edit, submit, priority, test, historyButton;
    private bool hasPosition, busy;
    private int priorityIndex = 1;
    private string confirmedSnapshot, reportSnapshot;
    public string ConfirmedSnapshot => confirmedSnapshot;
    private Vector3 lockedPosition;
    private IssueScanModel reportModel;
    private readonly string[] priorities = { "low", "medium", "high" };

    public void Initialize(Transform parent, ArProbe owner)
    {
        probe = owner;
        canvas = parent.GetComponentInParent<Canvas>();
        var panel = new GameObject("Report form", typeof(RectTransform), typeof(Image));
        reportPanel = panel;
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.94f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(0.5f, 0); rect.sizeDelta = new Vector2(0, 795);
        rect.anchoredPosition = new Vector2(0, 130);
        history = gameObject.AddComponent<IssueHistoryPanel>();
        history.Initialize(parent, probe, ShowHome);
        live = gameObject.AddComponent<IssueLiveUpdates>();
        live.Initialize(parent, probe, history);
        homeControls = NavigationPanel(parent, "首页入口", 120);
        ButtonAt(homeControls.transform, "扫描模型", 45, 0.35f, 0.65f, () => probe.OpenSavedScans());
        historyButton = ButtonAt(homeControls.transform, "新增问题", 45, 0.03f, 0.33f, BeginReport);
        ButtonAt(homeControls.transform, "设置", 45, 0.67f, 0.97f, () => {
            if (history.Busy) return;
            history.Hide(); homeControls.SetActive(false); settingsPanel.SetActive(true);
        });
        settingsPanel = NavigationPanel(parent, "设置", 760);
        address = Field(settingsPanel.transform, "输入 PC 显示的地址，如 http://192.168.1.8:18088", 590, 85, false);
        activeServer = PlayerPrefs.GetString("arwork.server", "http://127.0.0.1:18088");
        receiverId = PlayerPrefs.GetString("arwork.receiver-id", "");
        address.text = activeServer;
        settingsMessage = Label(settingsPanel.transform, 27);
        settingsMessage.rectTransform.anchorMin = new Vector2(0.03f, 0);
        settingsMessage.rectTransform.anchorMax = new Vector2(0.97f, 0);
        settingsMessage.rectTransform.pivot = new Vector2(0.5f, 0);
        settingsMessage.rectTransform.sizeDelta = new Vector2(0, 220);
        settingsMessage.rectTransform.anchoredPosition = new Vector2(0, 330);
        settingsMessage.text = "电脑启动后会显示手机连接地址与端口。手机和电脑需在同一局域网，输入完整地址后连接。";
        test = ButtonAt(settingsPanel.transform, "连接并保存", 220, 0.03f, 0.97f, () => StartCoroutine(TestConnection()));
        ButtonAt(settingsPanel.transform, "返回首页", 120, 0.03f, 0.97f, () => {
            if (busy) return; address.text = activeServer; ShowHome();
        });
        captureControls = NavigationPanel(parent, "采集操作", 170);
        ButtonAt(captureControls.transform, "返回首页", 25, 0.03f, 0.45f, () => { if (!NavigationLocked) ShowHome(); });
        capture = ButtonAt(captureControls.transform, "快照 / 下一步", 25, 0.5f, 0.97f, () => {
            if (!hasPosition || !probe.HasTrackedDraft) return;
            try { reportModel = probe.FreezeIssueModel(); }
            catch (Exception error) { Debug.LogWarning(error.Message); return; }
            lockedPosition = probe.CurrentPosition;
            TakeSnapshot(() => { reportSnapshot = confirmedSnapshot; PositionLocked = true; probe.LeaveCamera(); captureControls.SetActive(false); reportPanel.SetActive(true); RefreshControls(); });
        });
        previewPanel = NavigationPanel(parent, "快照确认", 1600);
        snapshotMessage = Label(previewPanel.transform, 26);
        snapshotMessage.rectTransform.anchorMin = new Vector2(0.03f, 0.10f); snapshotMessage.rectTransform.anchorMax = new Vector2(0.97f, 0.17f);
        snapshotMessage.rectTransform.offsetMin = snapshotMessage.rectTransform.offsetMax = Vector2.zero;
        snapshotMessage.text = "确认现场照片后填写问题，或重拍。";
        var previewFrame = new GameObject("照片预览区域", typeof(RectTransform)).GetComponent<RectTransform>();
        previewFrame.SetParent(previewPanel.transform, false);
        previewFrame.anchorMin = new Vector2(0.03f, 0.17f); previewFrame.anchorMax = new Vector2(0.97f, 0.98f);
        previewFrame.offsetMin = previewFrame.offsetMax = Vector2.zero;
        preview = new GameObject("现场快照", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        preview.transform.SetParent(previewFrame, false);
        preview.rectTransform.anchorMin = new Vector2(0.03f, 0.17f); preview.rectTransform.anchorMax = new Vector2(0.97f, 0.98f);
        preview.rectTransform.offsetMin = preview.rectTransform.offsetMax = Vector2.zero;
        preview.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        ButtonAt(previewPanel.transform, "重拍", 30, 0.03f, 0.48f, () => {
            ClearSnapshot(); previewPanel.SetActive(false); capturing = false;
            if (history.IsRemarking) history.ShowRemarkControls(); else captureControls.SetActive(true);
        });
        ButtonAt(previewPanel.transform, "确认 / 下一步", 30, 0.52f, 0.97f, () => {
            try {
                var bytes = snapshot.EncodeToJPG(80);
                if (bytes == null || bytes.Length > 6 * 1024 * 1024) throw new InvalidOperationException("照片超过 6 MiB，请重拍。");
                confirmedSnapshot = Convert.ToBase64String(bytes);
            } catch (Exception exception) { snapshotMessage.text = "图片处理失败，请重拍。"; Debug.LogException(exception); return; }
            previewPanel.SetActive(false); capturing = false; var confirm = snapshotConfirmed; snapshotConfirmed = null; confirm?.Invoke();
        });
        title = Field(rect, "问题标题（必填）", 500, 65, false);
        description = Field(rect, "补充描述（选填）", 385, 100, true);
        priority = ButtonAt(rect, "优先级：中", 310, 0.03f, 0.48f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length;
            priorityLabel.text = "优先级：" + InspectionUi.Priority(priorities[priorityIndex]);
        });
        priorityLabel = priority.GetComponentInChildren<Text>();
        edit = ButtonAt(rect, "调整位置", 310, 0.52f, 0.97f, EditOrCancel);
        editLabel = edit.GetComponentInChildren<Text>();
        submit = ButtonAt(rect, "保存到本地并返回首页", 235, 0.03f, 0.97f, () => StartCoroutine(Submit()));
        message = Label(rect, 27);
        var textRect = message.rectTransform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = new Vector2(1, 0);
        textRect.pivot = new Vector2(0.5f, 0); textRect.sizeDelta = new Vector2(-50, 155);
        textRect.anchoredPosition = new Vector2(0, 10);
        message.text = "先扫描物品并暂停，再点击实物位置放标记。";
        ButtonAt(rect, "返回首页（保留当前草稿）", 650, 0.03f, 0.97f, () => { if (!NavigationLocked) ShowHome(); });
        local = gameObject.AddComponent<LocalReports>();
        local.Initialize(parent, () => activeServer, () => history.RefreshLocalRows(), ShowHome);
        RestorePending();
        if (!string.IsNullOrEmpty(receiverId)) local.RememberReceiver(activeServer, receiverId);
        live.SetServer(activeServer);
        reportPanel.SetActive(false); settingsPanel.SetActive(false); captureControls.SetActive(false); previewPanel.SetActive(false);
        RefreshControls();
    }

    private void RestorePending()
    {
        string stored;
        try { stored = PendingReportStore.Read() ?? PlayerPrefs.GetString(PendingPreference, ""); }
        catch (Exception exception) { busy = true; message.text = "待确认上报读取失败，请保留应用数据。"; Debug.LogException(exception); return; }
        if (stored == "") return;
        try
        {
            var pending = JsonUtility.FromJson<PendingRequest>(stored);
            var draft = JsonUtility.FromJson<ReportPayload>(pending.body);
            if (string.IsNullOrEmpty(pending.key) || string.IsNullOrEmpty(pending.server) || draft.position == null)
                throw new ArgumentException("Invalid pending report");
            local.Add(pending.server, pending.endpoint ?? "/api/issues", pending.body, pending.key, true);
            PendingReportStore.Delete(); PlayerPrefs.DeleteKey(PendingPreference); PlayerPrefs.Save();
        }
        catch (Exception exception)
        {
            busy = true; PositionLocked = true;
            message.text = "待确认上报无法恢复，请保留应用数据并联系维护人员。";
            Debug.LogException(exception);
        }
    }

    public void MarkPositionSelected()
    {
        if (history != null && history.IsRemarking) history.MarkPositionSelected();
        else { draftServer = activeServer; draftReceiverId = receiverId; hasPosition = true; }
    }
    public void ClearPosition() { if (history != null && history.IsRemarking) history.ClearPosition(); else { hasPosition = false; RefreshControls(); } }

    private void EditOrCancel()
    {
        if (busy || draftServer != activeServer || draftReceiverId != receiverId) return;
        PositionLocked = false;
        message.text = "内容已保留，请点击表面调整位置。";
        reportPanel.SetActive(false); captureControls.SetActive(true); PositionLocked = false;
        ClearSnapshot(); probe.EnterCamera();
        RefreshControls();
    }

    private IEnumerator SelectReceiver(ReceiverInfo candidate)
    {
        if (busy || history.Busy || history.IsRemarking) yield break;
        busy = true; RefreshControls();
        settingsMessage.text = "正在核对接收端身份…";
        var task = ReceiverConnection.Verify(receiverClient, candidate.address, candidate.id);
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted || task.IsCanceled) settingsMessage.text = "连接失败：请核对电脑显示的 IP 和端口、同一 Wi-Fi 及电脑防火墙。原目的地和待上传资料保留。";
        else if (history.Busy || history.IsRemarking || probe.CameraActive)
            settingsMessage.text = "现场操作或保存正在进行，接收端切换已推迟，请稍后重试。";
        else {
            try {
                var receiver = task.Result;
                local.RememberReceiver(receiver.address, receiver.id);
                if (receiver.id == receiverId && receiver.address != activeServer) {
                    if (draftReceiverId == receiverId) draftServer = receiver.address;
                    history.Readdress(activeServer, receiver.address);
                    probe.ReaddressMarkers(activeServer, receiver.address);
                }
                activeServer = receiver.address; receiverId = receiver.id; address.text = activeServer;
                PlayerPrefs.SetString("arwork.server", activeServer);
                PlayerPrefs.SetString("arwork.receiver-id", receiverId); PlayerPrefs.Save();
                live.SetServer(activeServer);
                settingsMessage.text = "已连接：" + receiver.name + "\n" + activeServer + "\n其他电脑的待上传资料保持原目的地。";
            } catch (Exception error) { settingsMessage.text = "保存接收端失败，请保留应用数据。"; Debug.LogException(error); }
        }
        busy = false; RefreshControls();
    }

    private IEnumerator TestConnection()
    {
        if (busy) yield break;
        var value = address.text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) {
            settingsMessage.text = "地址格式有误，请使用 http://电脑IP:端口。"; yield break;
        }
        // At a remembered address require the remembered identity; another service cannot silently replace it.
        yield return SelectReceiver(new ReceiverInfo { address = value, id = value == activeServer && !string.IsNullOrEmpty(receiverId) ? receiverId : null });
    }

    private IEnumerator Submit()
    {
        if (busy || !positionLocked || !hasPosition) yield break;

        if (string.IsNullOrWhiteSpace(title.text)) { message.text = "请填写问题标题。"; yield break; }
        if (string.IsNullOrEmpty(reportSnapshot)) { message.text = "请返回现场拍摄并确认快照。"; yield break; }
        if (reportModel == null) { message.text = "请返回扫描并重新选点。"; yield break; }
        var point = lockedPosition;
        var payload = new ReportPayload { reportVersion = 2, model = reportModel, title = title.text.Trim(), description = description.text,
            snapshot = reportSnapshot, priority = priorities[priorityIndex], position = new IssueHistoryPanel.Position { x = point.x, y = point.y, z = point.z } };
        try {
            var body = JsonUtility.ToJson(payload);
            if (System.Text.Encoding.UTF8.GetByteCount(body) > 32 * 1024 * 1024) throw new InvalidOperationException("完整资料超过 32 MiB，请减少扫描范围后重试。");
            var entry = local.Add(draftServer ?? activeServer, "/api/reports", body, frozen: true, receiverIdentity: draftReceiverId);
            probe.KeepQueuedMarker(entry.server, entry.key);
        } catch (Exception error) {
            message.text = "本地保存失败，图片和内容仍保留，请重试。"; Debug.LogException(error); yield break;
        }
        // The durable queue owns the complete request now; another capture is independent.
        ClearSnapshot(); reportSnapshot = null; reportModel = null;
        hasPosition = false; PositionLocked = false;
        draftServer = null; draftReceiverId = null; title.text = ""; description.text = "";
        message.text = "已保存到本地，待上传。";
        RefreshControls(); ShowHome();
    }

    private void RefreshControls()
    {
        address.interactable = !busy;
        title.interactable = description.interactable = priority.interactable = positionLocked && !busy;
        submit.interactable = positionLocked && !busy;
        test.interactable = !busy;
        historyButton.interactable = !busy;
        edit.interactable = !busy && draftServer == activeServer && draftReceiverId == receiverId;
        editLabel.text = "调整位置";
    }

    private void Start() { ShowHome(); }

    public void ShowRemarkNavigation(bool remarking)
    {
        homeControls.SetActive(!remarking);
    }

    public void ShowHome()
    {
        local?.Hide();
        probe.LeaveCamera();
        // Keep the confirmed photo with the in-memory draft when returning home.
        reportPanel.SetActive(false); captureControls.SetActive(false); previewPanel.SetActive(false);
        settingsPanel.SetActive(false); homeControls.SetActive(true);
        history.Open(activeServer, receiverId);
    }

    private void BeginReport()
    {
        if (history.Busy) return;
        if (string.IsNullOrEmpty(receiverId)) {
            history.Hide(); homeControls.SetActive(false); settingsPanel.SetActive(true);
            settingsMessage.text = "请先输入 PC 显示的地址并连接；保存后可以离线采集。"; return;
        }
        history.Hide(); homeControls.SetActive(false);
        if (draftServer != null && (draftServer != activeServer || draftReceiverId != receiverId))
        {
            reportPanel.SetActive(true); message.text = "当前草稿属于 " + draftServer + "，提交将发送至该服务。可返回设置切回原服务继续采集。";
            RefreshControls(); return;
        }
        if (positionLocked || busy) { reportPanel.SetActive(true); return; }
        captureControls.SetActive(true); probe.EnterCamera();
    }

    private void Update()
    {
        if (capture != null) capture.interactable = !capturing && hasPosition && probe.HasTrackedDraft && !probe.SurfaceInteractionActive;
    }

    public void TakeSnapshot(Action confirmed)
    {
        if (capturing || !probe.CameraActive || !probe.HasTrackedDraft || probe.SurfaceInteractionActive ||
            UnityEngine.XR.ARFoundation.ARSession.state != UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking) return;
        snapshotConfirmed = confirmed; StartCoroutine(Capture());
    }

    private IEnumerator Capture()
    {
        capturing = true; captureControls.SetActive(false); history.HideRemarkControls();
        probe.SetSnapshotMode(true);
        canvas.enabled = false;
        yield return new WaitForEndOfFrame();
        try
        {
            ClearSnapshot(); snapshot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            snapshot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            snapshot.Apply(); preview.texture = snapshot;
            preview.GetComponent<AspectRatioFitter>().aspectRatio = (float)snapshot.width / snapshot.height;
            previewPanel.SetActive(true);
        }
        catch (Exception exception)
        {
            capturing = false;
            if (history.IsRemarking) history.ShowRemarkControls(); else captureControls.SetActive(true);
            Debug.LogException(exception);
        }
        finally { probe.SetSnapshotMode(false); canvas.enabled = true; }
    }

    private void OnDestroy() { receiverClient.Dispose(); }

    private void ClearSnapshot()
    {
        preview.texture = null;
        if (snapshot != null) Destroy(snapshot);
        snapshot = null; confirmedSnapshot = null;
    }

    private static GameObject NavigationPanel(Transform parent, string name, float height)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);
        obj.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 1f);
        var rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = new Vector2(1, 0); rect.pivot = new Vector2(0.5f, 0);
        rect.sizeDelta = new Vector2(0, height);
        return obj;
    }

    internal static Text Label(Transform parent, int size)
    {
        var label = new GameObject("Text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        label.transform.SetParent(parent, false); label.font = InspectionUi.Font;
        label.fontSize = size; label.color = Color.white; label.raycastTarget = false;
        label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(16, 6); label.rectTransform.offsetMax = new Vector2(-16, -6);
        return label;
    }

    private static RectTransform Place(GameObject obj, Transform parent, float y, float height, float min = 0.03f, float max = 0.97f)
    {
        obj.transform.SetParent(parent, false); var rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(min, 0); rect.anchorMax = new Vector2(max, 0); rect.pivot = new Vector2(0.5f, 0);
        rect.sizeDelta = new Vector2(0, height); rect.anchoredPosition = new Vector2(0, y); return rect;
    }

    internal static InputField Field(Transform parent, string hint, float y, float height, bool multiline)
    {
        var obj = new GameObject(hint, typeof(RectTransform), typeof(Image), typeof(InputField));
        Place(obj, parent, y, height); obj.GetComponent<Image>().color = new Color(0.16f, 0.23f, 0.28f);
        var field = obj.GetComponent<InputField>(); field.textComponent = Label(obj.transform, 29);
        var placeholder = Label(obj.transform, 27); placeholder.text = hint; placeholder.color = Color.gray;
        field.placeholder = placeholder; field.characterLimit = multiline ? 4000 : 300;
        field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
        return field;
    }

    internal static Button ButtonAt(Transform parent, string caption, float y, float min, float max, UnityEngine.Events.UnityAction action)
    {
        var obj = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(Button));
        Place(obj, parent, y, 60, min, max); obj.GetComponent<Image>().color = new Color(0.1f, 0.32f, 0.35f);
        var label = Label(obj.transform, 27); label.text = caption; label.alignment = TextAnchor.MiddleCenter;
        var button = obj.GetComponent<Button>(); button.onClick.AddListener(action); return button;
    }
}

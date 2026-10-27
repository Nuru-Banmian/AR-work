using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// Historical coordinates are display-only; remarking always requires a fresh AR hit.
public sealed class IssueHistoryPanel : MonoBehaviour
{
    [Serializable] public sealed class Position { public float x, y, z; }
    [Serializable] public sealed class Issue {
        public string id, title, description, priority, status, createdAt, updatedAt, snapshotUrl, modelUrl;
        public Position position;
    }
    [Serializable] private sealed class IssueResult { public Issue data; }
    [Serializable] private sealed class IssueList { public Issue[] data; }
    [Serializable] private sealed class Content { public string title, description, priority; }
    [Serializable] private sealed class Location { public Position position; public string snapshot; }

    public bool IsOpen => IsRemarking || (panel != null && panel.activeSelf);
    public bool IsRemarking { get; private set; }
    public bool Busy => busy && !loading;
    private bool loading;
    private Coroutine loadRoutine;
    private UnityWebRequest loadRequest;
    public bool RemarkLocked => busy || snapshotReady;
    private bool snapshotReady;
    private ArProbe probe;
    private GameObject remarkPanel;
    private Text remarkMessage;
    private Button remark, confirmRemark, cancelRemark;
    private bool hasRemarkPosition;
    private GameObject panel, listPage, detailPage;
    private RectTransform rows;
    private Text message, metadata, priorityLabel;
    private InputField title, description;
    private Button close, back, refresh, save, priority;
    private Action onClose;
    private bool busy, detailLoaded;
    public bool CanApplyLive => !busy;
    private Content loadedContent;
    private string server, selectedId, receiverScope;
    private readonly string[] priorities = { "low", "medium", "high" };
    private int priorityIndex;
    // A draft remains attached to its server and ID when navigating or retrying.
    private readonly Dictionary<string, Content> drafts = new Dictionary<string, Content>();
    private IssuePhotoView photoView;
    private Issue[] remoteRows = new Issue[0];
    private bool online;
    private Issue displayedIssue;
    private readonly Dictionary<string, string> syncTimes = new Dictionary<string, string>();
    private string SyncKey => receiverScope + "/" + (selectedId ?? "");
    private string SyncTime => server != null && syncTimes.TryGetValue(SyncKey, out var time) ? InspectionUi.Time(time) : "尚未同步";

    public void MarkSyncUnavailable(string address)
    {
        if (address != server) return;
        online = false;
        if (detailLoaded) RenderMetadata();
        if (!busy) SetBusy(false);
    }

    private void MarkSynced()
    {
        online = true; syncTimes[SyncKey] = DateTimeOffset.UtcNow.ToString("O");
    }

    private void RenderMetadata()
    {
        if (displayedIssue == null) return;
        metadata.text = "状态：" + InspectionUi.Status(displayedIssue.status) + "\n创建：" + InspectionUi.Time(displayedIssue.createdAt) +
            "  更新：" + InspectionUi.Time(displayedIssue.updatedAt) + "\n编号：" + displayedIssue.id +
            "\n最近同步：" + SyncTime + (online ? "" : "（离线或同步失败，旧状态）");
    }

    public void Initialize(Transform parent, ArProbe owner, Action returnToReport)
    {
        probe = owner;
        onClose = returnToReport;
        panel = new GameObject("All issues", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.98f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(0, 120); rect.offsetMax = new Vector2(0, -120);
        close = IssueReportForm.ButtonAt(rect, "记录首页", 10, 0.03f, 0.48f, Close);
        refresh = IssueReportForm.ButtonAt(rect, "刷新", 10, 0.52f, 0.97f, BeginLoad);
        message = IssueReportForm.Label(rect, 27);
        message.rectTransform.anchorMax = new Vector2(1, 0);
        message.rectTransform.sizeDelta = new Vector2(-40, 125);
        message.rectTransform.anchoredPosition = new Vector2(0, 140);

        listPage = Page(rect, "Issue list");
        var scroll = listPage.AddComponent<ScrollRect>();
        listPage.AddComponent<RectMask2D>();
        rows = new GameObject("Rows", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
        rows.SetParent(listPage.transform, false);
        rows.anchorMin = new Vector2(0, 1); rows.anchorMax = Vector2.one; rows.pivot = new Vector2(0.5f, 1);
        rows.sizeDelta = Vector2.zero;
        var layout = rows.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 16; layout.childControlHeight = true; layout.childForceExpandHeight = false;
        rows.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = rows; scroll.viewport = listPage.GetComponent<RectTransform>(); scroll.horizontal = false;

        detailPage = Page(rect, "Issue details");
        var detail = detailPage.GetComponent<RectTransform>();
        photoView = gameObject.AddComponent<IssuePhotoView>();
        photoView.Initialize(detail, parent);
        back = IssueReportForm.ButtonAt(detail, "返回列表", 0, 0.03f, 0.97f, () => {
            KeepDraft(); selectedId = null; ShowPage(); BeginLoad();
        });
        save = IssueReportForm.ButtonAt(detail, "保存内容", 80, 0.03f, 0.48f, () => StartCoroutine(Save()));
        remark = IssueReportForm.ButtonAt(detail, "重新标记", 80, 0.52f, 0.97f, BeginRemark);
        priority = IssueReportForm.ButtonAt(detail, "优先级", 160, 0.03f, 0.97f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length;
            priorityLabel.text = "优先级：" + InspectionUi.Priority(priorities[priorityIndex]); KeepDraft();
        });
        priorityLabel = priority.GetComponentInChildren<Text>();
        description = IssueReportForm.Field(detail, "补充描述（选填）", 240, 150, true);
        title = IssueReportForm.Field(detail, "问题标题（必填）", 410, 75, false);
        // Existing API content has no character limit. Never truncate it when opening the editor.
        title.characterLimit = description.characterLimit = 0;
        metadata = IssueReportForm.Label(detail, 26);
        metadata.rectTransform.offsetMin = new Vector2(25, 735);
        metadata.rectTransform.offsetMax = new Vector2(-25, -10);
        title.onValueChanged.AddListener(_ => KeepDraft());
        description.onValueChanged.AddListener(_ => KeepDraft());
        panel.SetActive(false);
        remarkPanel = new GameObject("Remark controls", typeof(RectTransform), typeof(Image));
        remarkPanel.transform.SetParent(parent, false);
        remarkPanel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.94f);
        var controls = remarkPanel.GetComponent<RectTransform>();
        controls.anchorMin = controls.anchorMax = new Vector2(0.5f, 0);
        controls.pivot = new Vector2(0.5f, 0); controls.sizeDelta = new Vector2(1080, 360);
        controls.anchoredPosition = new Vector2(0, 130);
        cancelRemark = IssueReportForm.ButtonAt(controls, "取消", 15, 0.03f, 0.48f, () => EndRemark(false));
        confirmRemark = IssueReportForm.ButtonAt(controls, "保存位置", 15, 0.52f, 0.97f, () => StartCoroutine(SavePosition()));
        remarkMessage = IssueReportForm.Label(controls, 27);
        remarkMessage.rectTransform.offsetMin = new Vector2(25, 95);
        remarkMessage.rectTransform.offsetMax = new Vector2(-25, -15);
        remarkPanel.SetActive(false);
    }

    private static GameObject Page(Transform parent, string name)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);
        obj.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.2f);
        var rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(15, 215); rect.offsetMax = new Vector2(-15, -15);
        return obj;
    }

    public void Open(string address, string identity = null)
    {
        if (Busy) return;
        CancelLoad(); KeepDraft();
        var nextScope = string.IsNullOrEmpty(identity) ? address : identity;
        bool changedServer = server != address || receiverScope != nextScope;
        receiverScope = nextScope;
        server = address; selectedId = null; detailLoaded = false; online = false;
        if (changedServer) remoteRows = new Issue[0];
        RenderList(remoteRows);
        panel.SetActive(true); ShowPage(); BeginLoad();
    }

    // Called only after verifying the same receiver identity at a different address.
    public void Readdress(string previous, string current)
    {
        if (server != previous || Busy || IsRemarking) return;
        CancelLoad(); KeepDraft();
        if (receiverScope == previous) {
            MoveDraftKeys(drafts, previous, current); MoveDraftKeys(syncTimes, previous, current);
            receiverScope = current;
        }
        server = current; online = false; photoView.Clear(); SetBusy(false);
        if (IsOpen) BeginLoad();
    }

    private static void MoveDraftKeys<T>(Dictionary<string, T> values, string previous, string current)
    {
        foreach (var key in new List<string>(values.Keys)) {
            if (!key.StartsWith(previous + "/", StringComparison.Ordinal)) continue;
            values[current + key.Substring(previous.Length)] = values[key]; values.Remove(key);
        }
    }

    public void Hide()
    {
        CancelLoad(); KeepDraft(); photoView.Clear(); panel.SetActive(false);
    }

    public void HideRemarkControls() { if (IsRemarking) remarkPanel.SetActive(false); }
    public void ShowRemarkControls() { if (IsRemarking) remarkPanel.SetActive(true); }

    private void Close()
    {
        if (busy) return;
        KeepDraft(); panel.SetActive(false); onClose();
    }

    private string DraftKey => receiverScope + "/" + selectedId;
    private void KeepDraft()
    {
        if (selectedId == null || !detailLoaded || !detailPage.activeSelf || busy) return;
        var draft = new Content { title = title.text, description = description.text, priority = priorities[priorityIndex] };
        if (draft.title == loadedContent.title && draft.description == loadedContent.description && draft.priority == loadedContent.priority)
            drafts.Remove(DraftKey);
        else drafts[DraftKey] = draft;
    }

    private void ShowPage()
    {
        photoView.Clear();
        listPage.SetActive(selectedId == null); detailPage.SetActive(selectedId != null);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        close.interactable = back.interactable = refresh.interactable = !value;
        save.interactable = priority.interactable = title.interactable = description.interactable = !value && detailLoaded && online;
        remark.interactable = !value && detailLoaded && online;
        cancelRemark.interactable = !value;
        confirmRemark.interactable = !value && hasRemarkPosition;
        foreach (var button in rows.GetComponentsInChildren<Button>()) button.interactable = !value;
    }

    private string Endpoint => server + "/api/issues" + (selectedId == null ? "" : "/" + Uri.EscapeDataString(selectedId));

    private void CancelLoad()
    {
        if (!loading) return;
        loadRequest?.Abort();
        if (loadRoutine != null) StopCoroutine(loadRoutine);
        loadRequest?.Dispose(); loadRequest = null;
        loadRoutine = null; loading = false; SetBusy(false);
    }

    private void BeginLoad() { if (!busy) loadRoutine = StartCoroutine(Load()); }

    private IEnumerator Load()
    {
        if (busy) yield break;
        GetComponent<IssueLiveUpdates>()?.Refresh();
        KeepDraft(); loading = true; SetBusy(true); message.text = "正在加载…";
        using (var request = UnityWebRequest.Get(Endpoint))
        {
            loadRequest = request;
            request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) {
                MarkSyncUnavailable(server);
                message.text = InspectionUi.Error(request) + "已同步记录只读，旧状态截至 " + SyncTime + "。本地记录仍可查看，可点击刷新重试。";
            }
            else
            {
                try
                {
                    if (selectedId == null)
                    {
                        var issues = JsonUtility.FromJson<IssueList>(request.downloadHandler.text)?.data;
                        if (issues == null) throw new ArgumentException("Missing issue list");
                        MarkSynced();
                        RenderList(issues);
                        message.text = issues.Length == 0 ? "暂无服务器问题，本地记录见列表。" : "共 " + issues.Length + " 个问题，点击查看详情。";
                    }
                    else
                    {
                        var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                        RequireIssue(issue);
                        MarkSynced();
                        RenderDetail(issue);
                        message.text = drafts.ContainsKey(DraftKey) ? "状态已更新，未保存的内容已保留。" : "可修改内容或重新标记。";
                    }
                }
                catch (ArgumentException) { MarkSyncUnavailable(server); message.text = "返回数据异常，已同步记录只读，草稿已保留，请刷新重试。"; }
            }
        }
        loadRequest = null; loading = false; SetBusy(false);
    }

    private void RenderList(Issue[] issues)
    {
        foreach (Transform child in rows) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        remoteRows = issues;
        GetComponent<LocalReports>()?.RenderRows(rows, issues);
        foreach (var issue in issues)
        {
            if (issue == null || string.IsNullOrEmpty(issue.id)) throw new ArgumentException("Invalid issue");
            var button = IssueReportForm.ButtonAt(rows, issue.title + "\n" + InspectionUi.Priority(issue.priority) + "优先级 · " + InspectionUi.Status(issue.status) + "\n已同步 · 最近同步：" + SyncTime, 0, 0, 1, () => {
                selectedId = issue.id; detailLoaded = false; title.SetTextWithoutNotify(""); description.SetTextWithoutNotify("");
                priorityIndex = 1; priorityLabel.text = "优先级：中"; metadata.text = "正在加载…";
                ShowPage(); BeginLoad();
            });
            button.gameObject.AddComponent<LayoutElement>().preferredHeight = 155;
            button.gameObject.AddComponent<IssueListPhoto>().Initialize(button, string.IsNullOrEmpty(issue.snapshotUrl) ? null :
                server + "/api/issues/" + Uri.EscapeDataString(issue.id) + "/snapshot?v=" + Uri.EscapeDataString(issue.updatedAt ?? ""));
        }
        listPage.GetComponent<ScrollRect>().verticalNormalizedPosition = 1;
    }

    public void RefreshLocalRows()
    {
        if (panel == null || !panel.activeSelf || selectedId != null) return;
        float scroll = listPage.GetComponent<ScrollRect>().verticalNormalizedPosition;
        RenderList(remoteRows);
        listPage.GetComponent<ScrollRect>().verticalNormalizedPosition = scroll;
        foreach (var button in rows.GetComponentsInChildren<Button>()) button.interactable = !busy;
    }

    public void ApplyLiveIssues(string address, Issue[] issues)
    {
        if (address != server || !IsOpen || busy) return;
        KeepDraft();
        MarkSynced();
        if (selectedId == null)
        {
            RenderList(issues);
            message.text = issues.Length == 0 ? "暂无服务器问题，本地记录见列表。" : "共 " + issues.Length + " 个问题，点击查看详情。";
        }
        else foreach (var issue in issues)
        {
            if (issue.id != selectedId) continue;
            RequireIssue(issue); RenderDetail(issue); break;
        }
        // Do not change remark position, replacement draft or save/cancel controls.
        if (!IsRemarking) SetBusy(false);
    }

    private void RequireIssue(Issue issue)
    {
        if (issue == null || issue.id != selectedId || issue.title == null || issue.description == null ||
            Array.IndexOf(priorities, issue.priority) < 0 || issue.position == null ||
            (issue.status != "open" && issue.status != "in_progress" && issue.status != "resolved"))
            throw new ArgumentException("Invalid issue response");
    }

    private void RenderDetail(Issue issue)
    {
        photoView.Show(server, issue.id, issue.snapshotUrl, issue.updatedAt);
        displayedIssue = issue; RenderMetadata();
        loadedContent = new Content { title = issue.title, description = issue.description, priority = issue.priority };
        var content = drafts.TryGetValue(DraftKey, out var draft) ? draft : loadedContent;
        title.SetTextWithoutNotify(content.title); description.SetTextWithoutNotify(content.description);
        priorityIndex = Array.IndexOf(priorities, content.priority);
        priorityLabel.text = "优先级：" + InspectionUi.Priority(priorities[priorityIndex]);
        detailLoaded = true;
    }

    private IEnumerator Save()
    {
        if (busy || selectedId == null || !detailLoaded || !online) yield break;
        KeepDraft();
        if (string.IsNullOrWhiteSpace(title.text)) { message.text = "请填写标题，其他内容已保留。"; yield break; }
        var body = JsonUtility.ToJson(new Content { title = title.text, description = description.text, priority = priorities[priorityIndex] });
        SetBusy(true); message.text = "正在保存…";
        using (var request = new UnityWebRequest(Endpoint + "/content", "PATCH"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) {
                MarkSyncUnavailable(server);
                message.text = InspectionUi.Error(request) + "结果待确认，草稿已保留。当前只读，请刷新核对后重试。";
            }
            else
            {
                try
                {
                    var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                    RequireIssue(issue); MarkSynced(); drafts.Remove(DraftKey); RenderDetail(issue);
                    message.text = "内容已保存 · " + InspectionUi.Status(issue.status);
                    Debug.Log("ARWORK_CONTENT_SAVED_ID=" + issue.id);
                }
                catch (ArgumentException) { message.text = "结果尚未确认，草稿已保留，请刷新核对或再次保存。"; }
            }
        }
        SetBusy(false);
    }

    private void BeginRemark()
    {
        if (busy || !detailLoaded || selectedId == null || !online) return;
        KeepDraft(); probe.BeginRemark(); probe.EnterCamera(); hasRemarkPosition = false; snapshotReady = false; IsRemarking = true;
        GetComponent<IssueReportForm>().ShowRemarkNavigation(true);
        panel.SetActive(false); remarkPanel.SetActive(true); SetBusy(false);
        remarkMessage.text = "请先扫描并暂停，再点击实物重新选点。保存成功后才替换原标记。";
    }

    public void MarkPositionSelected()
    {
        hasRemarkPosition = true; snapshotReady = false; confirmRemark.GetComponentInChildren<Text>().text = "快照 / 下一步"; confirmRemark.interactable = !busy;
        remarkMessage.text = "新位置已选中。点击保存位置，或取消保留原标记。";
    }

    public void ClearPosition()
    {
        hasRemarkPosition = false; snapshotReady = false; confirmRemark.interactable = false;
        remarkMessage.text = "请重新选点，原标记已保留。";
    }

    private void EndRemark(bool saved)
    {
        if (busy) return;
        probe.FinishRemark(saved, server, selectedId); probe.LeaveCamera(); IsRemarking = false; snapshotReady = false;
        GetComponent<IssueReportForm>().ShowRemarkNavigation(false);
        hasRemarkPosition = false; remarkPanel.SetActive(false); panel.SetActive(true);
        if (!saved) message.text = "已取消本次选点，原标记保留。请刷新核对已保存的位置。";
    }

    private IEnumerator SavePosition()
    {
        if (busy || !IsRemarking || !hasRemarkPosition) yield break;
        if (!snapshotReady)
        {
            GetComponent<IssueReportForm>().TakeSnapshot(() => {
                snapshotReady = true; remarkPanel.SetActive(true);
                confirmRemark.GetComponentInChildren<Text>().text = "保存位置";
                remarkMessage.text = "快照已确认，保存位置后返回详情。";
            });
            yield break;
        }
        if (UnityEngine.XR.ARFoundation.ARSession.state != UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking || !probe.HasTrackedDraft)
        { remarkMessage.text = "定位暂不可用，请重新扫描后保存。"; yield break; }
        var point = probe.CurrentPosition;
        var body = JsonUtility.ToJson(new Location { snapshot = GetComponent<IssueReportForm>().ConfirmedSnapshot, position = new Position { x = point.x, y = point.y, z = point.z } });
        bool succeeded = false;
        SetBusy(true); remarkMessage.text = "正在保存位置…";
        using (var request = new UnityWebRequest(Endpoint + "/position", "PATCH"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                remarkMessage.text = InspectionUi.Error(request) + "新位置仍为草稿，可重试；也可取消后刷新核对，结果可能已经保存。";
            else
            {
                try
                {
                    var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                    RequireIssue(issue);
                    if (issue.position.x != point.x || issue.position.y != point.y || issue.position.z != point.z)
                        throw new ArgumentException("Position mismatch");
                    RenderDetail(issue); succeeded = true;
                    message.text = "位置已保存 · " + InspectionUi.Status(issue.status) + "。内容草稿已保留。";
                    Debug.Log("ARWORK_REMARK_SAVED_ID=" + issue.id);
                }
                catch (ArgumentException) { remarkMessage.text = "结果尚未确认，新位置仍为草稿。请重试，或取消后刷新核对。"; }
            }
        }
        SetBusy(false);
        if (succeeded) EndRemark(true);
    }

}

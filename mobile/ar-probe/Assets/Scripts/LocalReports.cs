using System;
using System.Linq;
using System.Net.Http;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

// Local records share the home list; the server list replaces them by acknowledged ID.
public sealed class LocalReports : MonoBehaviour
{
    [Serializable] private sealed class Receipt { public IssueHistoryPanel.Issue data; }
    private ReportQueue queue;
    private ReportQueueSender sender;
    private HttpClient client;
    private Func<string> server;
    private Action changed, closed;
    private GameObject panel;
    private InputField title, description;
    private Button save, priority, retry;
    private Text info;
    private RawImage photo;
    private Texture2D texture;
    private string selected, failure;
    private string retryKey;
    private int priorityIndex;
    private bool sending, paused;
    private float nextAttempt;
    private readonly string[] priorities = { "low", "medium", "high" };
    public bool IsOpen => panel != null && panel.activeSelf;
    public string Error => failure;

    public void Initialize(Transform parent, Func<string> currentServer, Action onChanged, Action onClosed)
    {
        server = currentServer; changed = onChanged; closed = onClosed;
        try {
            queue = new ReportQueue(Path.Combine(Application.persistentDataPath, "report-queue-v1"));
            queue.List(server()); // fail closed on corrupt data
            sender = new ReportQueueSender(queue);
            client = ReceiverDiscovery.CreateClient(15);
        } catch (Exception error) { failure = "本地队列读取失败，请保留应用数据。"; Debug.LogException(error); }
        panel = new GameObject("本地问题详情", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 1);
        // The backdrop is modal, while controls avoid Android's bottom gesture area.
        var content = new GameObject("本地详情操作区", typeof(RectTransform)).GetComponent<RectTransform>();
        content.SetParent(rect, false);
        content.anchorMin = Vector2.zero; content.anchorMax = Vector2.one;
        content.offsetMin = new Vector2(0, 120); content.offsetMax = new Vector2(0, -120);
        rect = content;
        IssueReportForm.ButtonAt(rect, "返回首页", 10, 0.03f, 0.97f, () => { Hide(); closed(); });
        retry = IssueReportForm.ButtonAt(rect, "重试上传", 90, 0.52f, 0.97f, () => { retryKey = selected; nextAttempt = 0; Hide(); closed(); });
        save = IssueReportForm.ButtonAt(rect, "保存修改", 90, 0.03f, 0.48f, SaveEdit);
        priority = IssueReportForm.ButtonAt(rect, "优先级", 180, 0.03f, 0.97f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length; SetPriority();
        });
        description = IssueReportForm.Field(rect, "描述", 270, 120, true);
        title = IssueReportForm.Field(rect, "标题", 410, 75, false);
        title.characterLimit = description.characterLimit = 0;
        var frame = new GameObject("本地快照区域", typeof(RectTransform)).GetComponent<RectTransform>();
        frame.SetParent(rect, false);
        frame.anchorMin = new Vector2(0.05f, 0.46f); frame.anchorMax = new Vector2(0.95f, 0.73f);
        frame.offsetMin = frame.offsetMax = Vector2.zero;
        photo = new GameObject("本地快照", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        photo.transform.SetParent(frame, false);
        photo.rectTransform.anchorMin = Vector2.zero; photo.rectTransform.anchorMax = Vector2.one;
        photo.rectTransform.offsetMin = photo.rectTransform.offsetMax = Vector2.zero;
        photo.raycastTarget = false;
        photo.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        info = IssueReportForm.Label(rect, 26);
        info.rectTransform.anchorMin = new Vector2(0.03f, 0.75f); info.rectTransform.anchorMax = new Vector2(0.97f, 0.98f);
        info.rectTransform.offsetMin = info.rectTransform.offsetMax = Vector2.zero;
        panel.SetActive(false);
    }

    public ReportQueue.Entry Add(string address, string endpoint, string body, string key = null, bool frozen = false, string receiverIdentity = null)
    {
        if (failure != null) throw new IOException(failure);
        var entry = queue.Add(address, endpoint, body, key, frozen, receiverIdentity);
        nextAttempt = Time.realtimeSinceStartup + 1; changed(); return entry;
    }

    public void RememberReceiver(string address, string id)
    {
        if (failure != null) throw new IOException(failure);
        queue.RememberReceiver(address, id);
        nextAttempt = 0;
    }

    public void RenderRows(Transform rows, IssueHistoryPanel.Issue[] remote)
    {
        if (failure != null) {
            var label = IssueReportForm.Label(rows, 25); label.text = failure;
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 100; return;
        }
        try {
            foreach (var entry in queue.List(server())) {
                if (entry.state == "synced" && remote.Any(issue => issue.id == entry.serverId)) continue;
                var payload = JsonUtility.FromJson<ReportPayload>(entry.body);
                var button = IssueReportForm.ButtonAt(rows, payload.title + "\n" + State(entry) + " · " + ProcessingState(entry) +
                    "\n最近同步：" + SyncTime(entry), 0, 0, 1, () => Show(entry.key));
                button.gameObject.AddComponent<LayoutElement>().preferredHeight = 155;
                button.gameObject.AddComponent<IssueListPhoto>().Initialize(button, null, payload.snapshot);
            }
        } catch (Exception error) { failure = "本地记录读取失败，请保留应用数据。"; Debug.LogException(error); }
    }

    public static string State(ReportQueue.Entry entry) => entry.state == "pending" ? "待上传" :
        entry.state == "synced" ? "已同步" : entry.state == "uploading" ? "上传中 / 中断后将重试" : "上传失败";

    private static string SyncTime(ReportQueue.Entry entry) => entry.state != "synced" ? "尚未同步" :
        string.IsNullOrEmpty(entry.syncedAt) ? "未知（旧回执）" : InspectionUi.Time(entry.syncedAt);

    private static string ProcessingState(ReportQueue.Entry entry)
    {
        if (entry.state != "synced") return "待处理（本地）";
        var issue = JsonUtility.FromJson<Receipt>(entry.receipt)?.data;
        return "回执状态：" + (issue == null || string.IsNullOrEmpty(issue.status) ? "未知" : InspectionUi.Status(issue.status));
    }

    private void Show(string key)
    {
        try {
            var entry = queue.Get(key); var payload = JsonUtility.FromJson<ReportPayload>(entry.body);
            selected = key; title.text = payload.title; description.text = payload.description;
            priorityIndex = Array.IndexOf(priorities, payload.priority); SetPriority();
            title.interactable = description.interactable = priority.interactable = save.interactable = !entry.frozen;
            retry.interactable = entry.state != "synced";
            info.text = State(entry) + " · " + ProcessingState(entry) + "\n最近同步：" + SyncTime(entry) + "\n" +
                (entry.state == "synced" ? "本地回执只读，状态可能已过期；联网返回首页刷新后查看最新记录。" :
                    entry.frozen ? "请求已冻结，重试使用原内容。" : "尚未发送，可编辑内容。") +
                "\n" + entry.error + (entry.serverId == null ? "" : "\n服务器编号：" + entry.serverId);
            if (texture != null) Destroy(texture); texture = null; photo.enabled = false;
            if (!string.IsNullOrEmpty(payload.snapshot)) {
                texture = new Texture2D(2, 2);
                if (!texture.LoadImage(Convert.FromBase64String(payload.snapshot))) throw new InvalidDataException("Invalid photo");
                photo.texture = texture; photo.enabled = true;
                photo.GetComponent<AspectRatioFitter>().aspectRatio = (float)texture.width / texture.height;
            }
            panel.transform.SetAsLastSibling(); panel.SetActive(true);
        } catch (Exception error) { failure = "本地详情读取失败，请保留应用数据。"; Debug.LogException(error); changed(); }
    }

    private void SetPriority() { priority.GetComponentInChildren<Text>().text = "优先级：" + InspectionUi.Priority(priorities[priorityIndex]); }

    private void SaveEdit()
    {
        try {
            if (string.IsNullOrWhiteSpace(title.text)) { info.text = "请填写标题，输入已保留。"; return; }
            var entry = queue.Get(selected); var payload = JsonUtility.FromJson<ReportPayload>(entry.body);
            payload.title = title.text.Trim(); payload.description = description.text; payload.priority = priorities[priorityIndex];
            queue.Edit(selected, JsonUtility.ToJson(payload)); Hide(); nextAttempt = 0; closed(); changed();
        } catch (Exception error) { info.text = "保存失败，输入已保留。" + error.Message; }
    }

    public void Hide()
    {
        panel.SetActive(false); selected = null; photo.texture = null;
        if (texture != null) Destroy(texture); texture = null;
    }

    private async void Update()
    {
        if (failure != null || sender == null || sending || paused || (IsOpen && save.interactable) || Time.realtimeSinceStartup < nextAttempt) return;
        sending = true;
        try {
            var uploadServer = server();
            var task = sender.UploadNext(uploadServer, client, retryKey);
            retryKey = null;
            if (!task.IsCompleted) changed(); // Notify only when a request actually started.
            bool attempted = await task;
            if (this == null) return;
            foreach (var entry in queue.List(uploadServer).Where(item => item.state == "synced"))
                GetComponent<ArProbe>()?.AcknowledgeQueuedMarker(uploadServer, entry.key, entry.serverId);
            nextAttempt = Time.realtimeSinceStartup + (attempted ? 2 : 5);
            if (IsOpen && !save.interactable) Show(selected);
            if (attempted) changed();
            if (attempted) GetComponent<IssueLiveUpdates>()?.Refresh();
        } catch (Exception error) { failure = "队列保存或读取失败，请保留应用数据。"; Debug.LogException(error); changed(); }
        finally { sending = false; }
    }

    private void OnApplicationPause(bool value) { paused = value; if (!value) nextAttempt = 0; }
    private void OnDestroy() { client?.Dispose(); if (texture != null) Destroy(texture); }
}

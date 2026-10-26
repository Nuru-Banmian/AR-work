using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public sealed class IssueReportForm : MonoBehaviour
{
    [Serializable] private sealed class Position { public float x, y, z; }
    [Serializable] private sealed class CreateIssue { public string title, description, priority; public Position position; }
    [Serializable] private sealed class SavedIssue { public string id, title, priority, status; }
    [Serializable] private sealed class Success { public SavedIssue data; }
    [Serializable] private sealed class HealthData { public string status; }
    [Serializable] private sealed class Health { public HealthData data; }
    [Serializable] private sealed class PendingRequest { public string key, body, server; }
    private const string PendingPreference = "arwork.pending-report";

    private bool positionLocked;
    public bool PositionLocked { get => history != null && history.IsRemarking ? history.RemarkLocked : positionLocked || (history != null && history.IsOpen); private set => positionLocked = value; }
    private IssueHistoryPanel history;
    private IssueLiveUpdates live;
    private ArProbe probe;
    private InputField address, title, description;
    private Text message, priorityLabel, editLabel;
    private Button edit, submit, priority, test, historyButton;
    private bool hasPosition, busy, saved;
    private int priorityIndex = 1;
    private string requestKey, requestBody, requestAddress;
    private Vector3 lockedPosition;
    private readonly string[] priorities = { "low", "medium", "high" };

    public void Initialize(Transform parent, ArProbe owner)
    {
        probe = owner;
        var panel = new GameObject("Report form", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.94f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(0.5f, 0); rect.sizeDelta = new Vector2(0, 795);
        rect.anchoredPosition = new Vector2(0, 130);
        history = gameObject.AddComponent<IssueHistoryPanel>();
        history.Initialize(parent, probe, () => panel.SetActive(true));
        live = gameObject.AddComponent<IssueLiveUpdates>();
        live.Initialize(parent, probe, history);
        historyButton = ButtonAt(rect, "问题列表", 725, 0.03f, 0.97f, () => {
            if (busy || !SaveAddress()) return;
            panel.SetActive(false);
            history.Open(address.text);
        });
        address = Field(rect, "服务地址，如 http://电脑IP:8080", 650, 60, false);
        address.text = PlayerPrefs.GetString("arwork.server", "http://192.168.1.100:8080");
        address.onEndEdit.AddListener(value => SaveAddress());
        test = ButtonAt(rect, "测试连接", 575, 0.03f, 0.97f, () => StartCoroutine(TestConnection()));
        title = Field(rect, "问题标题（必填）", 500, 65, false);
        description = Field(rect, "补充描述（选填）", 385, 100, true);
        priority = ButtonAt(rect, "优先级：中", 310, 0.03f, 0.48f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length;
            priorityLabel.text = "优先级：" + InspectionUi.Priority(priorities[priorityIndex]);
        });
        priorityLabel = priority.GetComponentInChildren<Text>();
        edit = ButtonAt(rect, "填写问题", 310, 0.52f, 0.97f, EditOrCancel);
        editLabel = edit.GetComponentInChildren<Text>();
        submit = ButtonAt(rect, "提交问题", 235, 0.03f, 0.97f, () => StartCoroutine(Submit()));
        message = Label(rect, 27);
        var textRect = message.rectTransform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = new Vector2(1, 0);
        textRect.pivot = new Vector2(0.5f, 0); textRect.sizeDelta = new Vector2(-50, 155);
        textRect.anchoredPosition = new Vector2(0, 10);
        message.text = "先点击地面或墙面选点，再填写问题。";
        RestorePending();
        SaveAddress();
        RefreshControls();
    }

    private void RestorePending()
    {
        string stored = PlayerPrefs.GetString(PendingPreference, "");
        if (stored == "") return;
        try
        {
            var pending = JsonUtility.FromJson<PendingRequest>(stored);
            var draft = JsonUtility.FromJson<CreateIssue>(pending.body);
            if (string.IsNullOrEmpty(pending.key) || string.IsNullOrEmpty(pending.server) || draft.position == null)
                throw new ArgumentException("Invalid pending report");
            requestKey = pending.key; requestBody = pending.body; requestAddress = pending.server;
            address.text = pending.server; title.text = draft.title; description.text = draft.description;
            priorityIndex = Array.IndexOf(priorities, draft.priority);
            if (priorityIndex < 0) throw new ArgumentException("Invalid pending priority");
            priorityLabel.text = "优先级：" + InspectionUi.Priority(draft.priority);
            PositionLocked = hasPosition = true;
            message.text = "有待确认的上报，请点击提交重试，不会重复创建。";
        }
        catch (Exception exception)
        {
            busy = true; PositionLocked = true;
            message.text = "待确认上报无法恢复，请保留应用数据并联系维护人员。";
            Debug.LogException(exception);
        }
    }

    public void MarkPositionSelected() { if (history != null && history.IsRemarking) history.MarkPositionSelected(); else hasPosition = true; }
    public void ClearPosition() { if (history != null && history.IsRemarking) history.ClearPosition(); else { hasPosition = false; RefreshControls(); } }

    private void EditOrCancel()
    {
        if (busy || (!saved && requestKey != null)) return;
        if (saved)
        {
            saved = false; hasPosition = false; PositionLocked = false;
            requestKey = requestBody = requestAddress = null;
            title.text = ""; description.text = "";
            message.text = "请选取下一个位置。已提交的标记会保留。";
        }
        else if (PositionLocked) { PositionLocked = false; message.text = "内容已保留，请点击表面调整位置。"; }
        else if (!hasPosition) { message.text = "请先点击地面或墙面选点。"; }
        else { lockedPosition = probe.CurrentPosition; PositionLocked = true; message.text = "位置已确认，请填写问题。"; }
        RefreshControls();
    }

    private bool SaveAddress()
    {
        var value = address.text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        { message.text = "地址格式有误，请填写 http://电脑局域网IP:8080。"; return false; }
        address.text = value;
        PlayerPrefs.SetString("arwork.server", value); PlayerPrefs.Save();
        live.SetServer(value);
        return true;
    }

    private IEnumerator TestConnection()
    {
        if (busy || !SaveAddress()) yield break;
        live.Refresh();
        busy = true; RefreshControls(); message.text = "正在测试连接…";
        using (var request = UnityWebRequest.Get(address.text + "/health"))
        {
            request.timeout = 10;
            yield return request.SendWebRequest();
            bool healthy = false;
            if (request.result == UnityWebRequest.Result.Success)
            {
                try { healthy = JsonUtility.FromJson<Health>(request.downloadHandler.text)?.data?.status == "ok"; } catch (ArgumentException) { }
            }
            message.text = healthy
                ? "连接成功。" : "连接失败，请检查服务地址、网络和电脑防火墙。";
        }
        busy = false; RefreshControls();
    }

    private IEnumerator Submit()
    {
        if (busy || saved || !PositionLocked || !hasPosition) yield break;
        if (requestKey == null && !SaveAddress()) yield break;
        if (string.IsNullOrWhiteSpace(title.text)) { message.text = "请填写问题标题。"; yield break; }
        if (requestKey == null)
        {
            var point = lockedPosition;
            var payload = new CreateIssue { title = title.text.Trim(), description = description.text,
                priority = priorities[priorityIndex], position = new Position { x = point.x, y = point.y, z = point.z } };
            requestKey = Guid.NewGuid().ToString("N");
            requestBody = JsonUtility.ToJson(payload);
            requestAddress = address.text;
            PlayerPrefs.SetString(PendingPreference, JsonUtility.ToJson(new PendingRequest {
                key = requestKey, body = requestBody, server = requestAddress }));
            PlayerPrefs.Save();
        }
        busy = true; RefreshControls(); message.text = "正在提交…";
        using (var request = new UnityWebRequest(requestAddress + "/api/issues", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Idempotency-Key", requestKey);
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                SavedIssue issue = null;
                try { issue = JsonUtility.FromJson<Success>(request.downloadHandler.text)?.data; } catch (ArgumentException) { }
                if (issue != null && !string.IsNullOrEmpty(issue.id))
                {
                    saved = true; probe.KeepSubmittedMarker(requestAddress, issue.id);
                    live.Refresh();
                    PlayerPrefs.DeleteKey(PendingPreference); PlayerPrefs.Save();
                    message.text = "上报成功：" + issue.title + "\n" + InspectionUi.Priority(issue.priority) + "优先级 · " + InspectionUi.Status(issue.status);
                    Debug.Log("ARWORK_SAVED_ID=" + issue.id);
                }
                else message.text = "尚未确认提交结果，内容已保留，请再次提交。";
            }
            else
            {
                message.text = InspectionUi.Error(request) + "内容已保留，请再次提交。";
            }
        }
        busy = false; RefreshControls();
    }

    private void RefreshControls()
    {
        bool pending = requestKey != null && !saved;
        address.interactable = !busy && !pending;
        title.interactable = description.interactable = priority.interactable = PositionLocked && !busy && !saved && !pending;
        submit.interactable = PositionLocked && !busy && !saved;
        test.interactable = !busy;
        historyButton.interactable = !busy;
        edit.interactable = !busy && !pending;
        editLabel.text = saved ? "继续上报" : pending ? "等待重试" : PositionLocked ? "调整位置" : "填写问题";
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

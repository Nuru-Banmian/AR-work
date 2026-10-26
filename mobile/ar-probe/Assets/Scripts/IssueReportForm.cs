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
    [Serializable] private sealed class ApiError { public string code, message; }
    [Serializable] private sealed class Failure { public ApiError error; }
    [Serializable] private sealed class HealthData { public string status; }
    [Serializable] private sealed class Health { public HealthData data; }

    public bool PositionLocked { get; private set; }
    private ArProbe probe;
    private InputField address, title, description;
    private Text message, priorityLabel, editLabel;
    private Button edit, submit, priority, test;
    private bool hasPosition, busy, saved;
    private int priorityIndex = 1;
    private readonly string[] priorities = { "low", "medium", "high" };

    public void Initialize(Transform parent, ArProbe owner)
    {
        probe = owner;
        var panel = new GameObject("Report form", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.94f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(0.5f, 0); rect.sizeDelta = new Vector2(0, 720);
        rect.anchoredPosition = new Vector2(0, 130);
        address = Field(rect, "Server URL (http://PC-IP:8080)", 650, 60, false);
        address.text = PlayerPrefs.GetString("arwork.server", "http://192.168.1.100:8080");
        address.onEndEdit.AddListener(value => SaveAddress());
        test = ButtonAt(rect, "TEST CONNECTION", 575, 0.03f, 0.97f, () => StartCoroutine(TestConnection()));
        title = Field(rect, "Title (required)", 500, 65, false);
        description = Field(rect, "Description (optional)", 385, 100, true);
        priority = ButtonAt(rect, "Priority: medium", 310, 0.03f, 0.48f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length;
            priorityLabel.text = "Priority: " + priorities[priorityIndex];
        });
        priorityLabel = priority.GetComponentInChildren<Text>();
        edit = ButtonAt(rect, "EDIT REPORT", 310, 0.52f, 0.97f, EditOrCancel);
        editLabel = edit.GetComponentInChildren<Text>();
        submit = ButtonAt(rect, "SUBMIT ISSUE", 235, 0.03f, 0.97f, () => StartCoroutine(Submit()));
        message = Label(rect, 27);
        var textRect = message.rectTransform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = new Vector2(1, 0);
        textRect.pivot = new Vector2(0.5f, 0); textRect.sizeDelta = new Vector2(-50, 155);
        textRect.anchoredPosition = new Vector2(0, 10);
        message.text = "Scan and tap a horizontal surface, then EDIT REPORT. Session coordinates are not persistent world locations.";
        RefreshControls();
    }

    public void MarkPositionSelected() { hasPosition = true; }
    public void ClearPosition() { hasPosition = false; RefreshControls(); }

    private void EditOrCancel()
    {
        if (busy) return;
        if (saved)
        {
            saved = false; hasPosition = false; PositionLocked = false;
            title.text = ""; description.text = "";
            message.text = "Scan and select the next position. Previous marker stays in this AR session.";
        }
        else if (PositionLocked) { PositionLocked = false; message.text = "Draft kept. Tap a surface to adjust position."; }
        else if (!hasPosition) { message.text = "Select a horizontal surface first."; }
        else { PositionLocked = true; message.text = "Position locked. Enter title, description and priority."; }
        RefreshControls();
    }

    private bool SaveAddress()
    {
        var value = address.text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        { message.text = "Invalid server URL. Use http://PC-LAN-IP:8080 (not phone localhost)."; return false; }
        address.text = value;
        PlayerPrefs.SetString("arwork.server", value); PlayerPrefs.Save();
        return true;
    }

    private IEnumerator TestConnection()
    {
        if (busy || !SaveAddress()) yield break;
        busy = true; RefreshControls(); message.text = "Testing connection...";
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
                ? "Connection successful." : "Connection failed: " + request.error + ". Check address, Wi-Fi and firewall.";
        }
        busy = false; RefreshControls();
    }

    private IEnumerator Submit()
    {
        if (busy || saved || !PositionLocked || !hasPosition || !SaveAddress()) yield break;
        if (string.IsNullOrWhiteSpace(title.text)) { message.text = "Title is required."; yield break; }
        var point = probe.CurrentPosition;
        var payload = new CreateIssue { title = title.text.Trim(), description = description.text,
            priority = priorities[priorityIndex], position = new Position { x = point.x, y = point.y, z = point.z } };
        busy = true; RefreshControls(); message.text = "Saving issue...";
        using (var request = new UnityWebRequest(address.text + "/api/issues", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                SavedIssue issue = null;
                try { issue = JsonUtility.FromJson<Success>(request.downloadHandler.text)?.data; } catch (ArgumentException) { }
                if (issue != null && !string.IsNullOrEmpty(issue.id))
                {
                    saved = true; probe.KeepSubmittedMarker();
                    message.text = "Saved ID: " + issue.id + "\n" + issue.title + " | " + issue.priority + " | " + issue.status;
                    Debug.Log("ARWORK_SAVED_ID=" + issue.id);
                }
                else message.text = "Invalid server response. Draft kept; check list before retrying.";
            }
            else
            {
                string detail = request.error;
                try { detail = JsonUtility.FromJson<Failure>(request.downloadHandler.text)?.error?.message ?? detail; } catch (ArgumentException) { }
                message.text = "Save failed: " + detail + ". Draft kept. Check list before retrying if result is uncertain.";
            }
        }
        busy = false; RefreshControls();
    }

    private void RefreshControls()
    {
        address.interactable = !busy;
        title.interactable = description.interactable = priority.interactable = PositionLocked && !busy && !saved;
        submit.interactable = PositionLocked && !busy && !saved;
        test.interactable = edit.interactable = !busy;
        editLabel.text = saved ? "NEW ISSUE" : PositionLocked ? "BACK TO POINT" : "EDIT REPORT";
    }

    private static Text Label(Transform parent, int size)
    {
        var label = new GameObject("Text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        label.transform.SetParent(parent, false); label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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

    private static InputField Field(Transform parent, string hint, float y, float height, bool multiline)
    {
        var obj = new GameObject(hint, typeof(RectTransform), typeof(Image), typeof(InputField));
        Place(obj, parent, y, height); obj.GetComponent<Image>().color = new Color(0.16f, 0.23f, 0.28f);
        var field = obj.GetComponent<InputField>(); field.textComponent = Label(obj.transform, 29);
        var placeholder = Label(obj.transform, 27); placeholder.text = hint; placeholder.color = Color.gray;
        field.placeholder = placeholder; field.characterLimit = multiline ? 4000 : 300;
        field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
        return field;
    }

    private static Button ButtonAt(Transform parent, string caption, float y, float min, float max, UnityEngine.Events.UnityAction action)
    {
        var obj = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(Button));
        Place(obj, parent, y, 60, min, max); obj.GetComponent<Image>().color = new Color(0.1f, 0.32f, 0.35f);
        var label = Label(obj.transform, 27); label.text = caption; label.alignment = TextAnchor.MiddleCenter;
        var button = obj.GetComponent<Button>(); button.onClick.AddListener(action); return button;
    }
}

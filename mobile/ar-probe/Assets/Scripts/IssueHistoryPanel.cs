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
    [Serializable] private sealed class Position { public float x, y, z; }
    [Serializable] private sealed class Issue {
        public string id, title, description, priority, status, createdAt, updatedAt;
        public Position position;
    }
    [Serializable] private sealed class IssueResult { public Issue data; }
    [Serializable] private sealed class IssueList { public Issue[] data; }
    [Serializable] private sealed class Content { public string title, description, priority; }
    [Serializable] private sealed class Location { public Position position; }
    [Serializable] private sealed class ApiError { public string code, message; }
    [Serializable] private sealed class Failure { public ApiError error; }

    public bool IsOpen => IsRemarking || (panel != null && panel.activeSelf);
    public bool IsRemarking { get; private set; }
    public bool RemarkLocked => busy;
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
    private Content loadedContent;
    private string server, selectedId;
    private readonly string[] priorities = { "low", "medium", "high" };
    private int priorityIndex;
    // A draft remains attached to its server and ID when navigating or retrying.
    private readonly Dictionary<string, Content> drafts = new Dictionary<string, Content>();

    public void Initialize(Transform parent, ArProbe owner, Action returnToReport)
    {
        probe = owner;
        onClose = returnToReport;
        panel = new GameObject("All issues", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        panel.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.98f);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(0, 130); rect.offsetMax = new Vector2(0, -390);
        close = IssueReportForm.ButtonAt(rect, "BACK TO AR", 10, 0.03f, 0.48f, Close);
        refresh = IssueReportForm.ButtonAt(rect, "REFRESH", 10, 0.52f, 0.97f, () => StartCoroutine(Load()));
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
        back = IssueReportForm.ButtonAt(detail, "BACK TO LIST (KEEP DRAFT)", 0, 0.03f, 0.97f, () => {
            KeepDraft(); selectedId = null; ShowPage(); StartCoroutine(Load());
        });
        save = IssueReportForm.ButtonAt(detail, "SAVE CONTENT", 80, 0.03f, 0.48f, () => StartCoroutine(Save()));
        remark = IssueReportForm.ButtonAt(detail, "REMARK IN AR", 80, 0.52f, 0.97f, BeginRemark);
        priority = IssueReportForm.ButtonAt(detail, "Priority", 160, 0.03f, 0.97f, () => {
            priorityIndex = (priorityIndex + 1) % priorities.Length;
            priorityLabel.text = "Priority: " + priorities[priorityIndex]; KeepDraft();
        });
        priorityLabel = priority.GetComponentInChildren<Text>();
        description = IssueReportForm.Field(detail, "Description (optional)", 240, 150, true);
        title = IssueReportForm.Field(detail, "Title (required)", 410, 75, false);
        // Existing API content has no character limit. Never truncate it when opening the editor.
        title.characterLimit = description.characterLimit = 0;
        metadata = IssueReportForm.Label(detail, 26);
        metadata.rectTransform.offsetMin = new Vector2(25, 505);
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
        cancelRemark = IssueReportForm.ButtonAt(controls, "CANCEL", 15, 0.03f, 0.48f, () => EndRemark(false));
        confirmRemark = IssueReportForm.ButtonAt(controls, "SAVE POSITION", 15, 0.52f, 0.97f, () => StartCoroutine(SavePosition()));
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

    public void Open(string address)
    {
        server = address; selectedId = null; panel.SetActive(true); ShowPage(); StartCoroutine(Load());
    }

    private void Close()
    {
        if (busy) return;
        KeepDraft(); panel.SetActive(false); onClose();
    }

    private string DraftKey => server + "/" + selectedId;
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
        listPage.SetActive(selectedId == null); detailPage.SetActive(selectedId != null);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        close.interactable = back.interactable = refresh.interactable = !value;
        save.interactable = priority.interactable = title.interactable = description.interactable = !value && detailLoaded;
        remark.interactable = !value && detailLoaded;
        cancelRemark.interactable = !value;
        confirmRemark.interactable = !value && hasRemarkPosition;
        foreach (var button in rows.GetComponentsInChildren<Button>()) button.interactable = !value;
    }

    private string Endpoint => server + "/api/issues" + (selectedId == null ? "" : "/" + Uri.EscapeDataString(selectedId));

    private IEnumerator Load()
    {
        if (busy) yield break;
        KeepDraft(); SetBusy(true); message.text = "Loading latest issues...";
        using (var request = UnityWebRequest.Get(Endpoint))
        {
            request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) message.text = Error(request) + " Draft kept. REFRESH to retry.";
            else
            {
                try
                {
                    if (selectedId == null)
                    {
                        var issues = JsonUtility.FromJson<IssueList>(request.downloadHandler.text)?.data;
                        if (issues == null) throw new ArgumentException("Missing issue list");
                        RenderList(issues);
                        message.text = issues.Length == 0 ? "No issues yet. Return to AR to report one." : "All issues: " + issues.Length + ". Tap to view/edit. Historical coordinates do not restore AR markers.";
                    }
                    else
                    {
                        var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                        RequireIssue(issue);
                        RenderDetail(issue);
                        message.text = drafts.ContainsKey(DraftKey) ? "Latest status loaded. Unsaved content draft kept." : "Latest details loaded. Edit content below.";
                    }
                }
                catch (ArgumentException) { message.text = "Invalid server response. Draft kept. REFRESH to retry."; }
            }
        }
        SetBusy(false);
    }

    private void RenderList(Issue[] issues)
    {
        foreach (Transform child in rows) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        foreach (var issue in issues)
        {
            if (issue == null || string.IsNullOrEmpty(issue.id)) throw new ArgumentException("Invalid issue");
            var button = IssueReportForm.ButtonAt(rows, issue.title + "\n" + issue.priority + " | " + issue.status + "\nID: " + issue.id, 0, 0, 1, () => {
                selectedId = issue.id; detailLoaded = false; title.SetTextWithoutNotify(""); description.SetTextWithoutNotify("");
                priorityIndex = 1; priorityLabel.text = "Priority: medium"; metadata.text = "Loading ID: " + selectedId;
                ShowPage(); StartCoroutine(Load());
            });
            button.gameObject.AddComponent<LayoutElement>().preferredHeight = 155;
        }
        listPage.GetComponent<ScrollRect>().verticalNormalizedPosition = 1;
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
        metadata.text = "ID: " + issue.id + "\nStatus: " + issue.status + "\nCreated: " + issue.createdAt +
            "\nUpdated: " + issue.updatedAt + "\nHistorical session position (m): " +
            issue.position.x + ", " + issue.position.y + ", " + issue.position.z + "\nNo AR marker restored from history.";
        loadedContent = new Content { title = issue.title, description = issue.description, priority = issue.priority };
        var content = drafts.TryGetValue(DraftKey, out var draft) ? draft : loadedContent;
        title.SetTextWithoutNotify(content.title); description.SetTextWithoutNotify(content.description);
        priorityIndex = Array.IndexOf(priorities, content.priority);
        priorityLabel.text = "Priority: " + priorities[priorityIndex];
        detailLoaded = true;
    }

    private IEnumerator Save()
    {
        if (busy || selectedId == null || !detailLoaded) yield break;
        KeepDraft();
        if (string.IsNullOrWhiteSpace(title.text)) { message.text = "Title is required. Draft kept."; yield break; }
        var body = JsonUtility.ToJson(new Content { title = title.text, description = description.text, priority = priorities[priorityIndex] });
        SetBusy(true); message.text = "Saving content...";
        using (var request = new UnityWebRequest(Endpoint + "/content", "PATCH"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) message.text = Error(request) + " Draft kept. SAVE CONTENT to retry.";
            else
            {
                try
                {
                    var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                    RequireIssue(issue); drafts.Remove(DraftKey); RenderDetail(issue);
                    message.text = "Content saved. ID: " + issue.id + ". Status: " + issue.status;
                    Debug.Log("ARWORK_CONTENT_SAVED_ID=" + issue.id);
                }
                catch (ArgumentException) { message.text = "Save result uncertain. Draft kept. REFRESH to check status; SAVE CONTENT to retry."; }
            }
        }
        SetBusy(false);
    }

    private void BeginRemark()
    {
        if (busy || !detailLoaded || selectedId == null) return;
        KeepDraft(); probe.BeginRemark(); hasRemarkPosition = false; IsRemarking = true;
        panel.SetActive(false); remarkPanel.SetActive(true); SetBusy(false);
        remarkMessage.text = "Remark ID: " + selectedId + "\nTap a floor or wall to select a NEW position. Old saved marker stays until success. Content draft kept.";
    }

    public void MarkPositionSelected()
    {
        hasRemarkPosition = true; confirmRemark.interactable = !busy;
        remarkMessage.text = "Replacement DRAFT for ID: " + selectedId + "\nPosition (m): " + probe.CurrentPosition.ToString("F3") + "\nSAVE POSITION to replace, or CANCEL to discard.";
    }

    public void ClearPosition()
    {
        hasRemarkPosition = false; confirmRemark.interactable = false;
        remarkMessage.text = "Replacement draft cleared. Tap a floor or wall. Original saved marker kept.";
    }

    private void EndRemark(bool saved)
    {
        if (busy) return;
        probe.FinishRemark(saved, server, selectedId); IsRemarking = false;
        hasRemarkPosition = false; remarkPanel.SetActive(false); panel.SetActive(true);
        if (!saved) message.text = "Replacement draft discarded. REFRESH to check latest saved position.";
    }

    private IEnumerator SavePosition()
    {
        if (busy || !IsRemarking || !hasRemarkPosition) yield break;
        if (UnityEngine.XR.ARFoundation.ARSession.state != UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking || !probe.HasTrackedDraft)
        { remarkMessage.text = "Tracking unavailable. Scan the surface again before saving."; yield break; }
        var point = probe.CurrentPosition;
        var body = JsonUtility.ToJson(new Location { position = new Position { x = point.x, y = point.y, z = point.z } });
        bool succeeded = false;
        SetBusy(true); remarkMessage.text = "Saving replacement position...";
        using (var request = new UnityWebRequest(Endpoint + "/position", "PATCH"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json"); request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                remarkMessage.text = Error(request) + " Replacement remains a DRAFT. Retry or cancel and REFRESH; a lost response may already have saved.";
            else
            {
                try
                {
                    var issue = JsonUtility.FromJson<IssueResult>(request.downloadHandler.text)?.data;
                    RequireIssue(issue);
                    if (issue.position.x != point.x || issue.position.y != point.y || issue.position.z != point.z)
                        throw new ArgumentException("Position mismatch");
                    RenderDetail(issue); succeeded = true;
                    message.text = "Position saved. ID: " + issue.id + ". Status: " + issue.status + ". Content draft kept.";
                    Debug.Log("ARWORK_REMARK_SAVED_ID=" + issue.id);
                }
                catch (ArgumentException) { remarkMessage.text = "Save result uncertain. Replacement remains a DRAFT. Retry or cancel and REFRESH to check saved position."; }
            }
        }
        SetBusy(false);
        if (succeeded) EndRemark(true);
    }

    private static string Error(UnityWebRequest request)
    {
        string detail = request.error;
        try { var error = JsonUtility.FromJson<Failure>(request.downloadHandler.text)?.error;
            if (error != null) detail = error.code + ": " + error.message;
        } catch (ArgumentException) { }
        return "Request failed (" + request.responseCode + "): " + detail + ".";
    }
}

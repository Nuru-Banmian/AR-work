// Run with Unity -batchmode -projectPath <project> -executeMethod InspectionNavigationChecks.Run
// Uses two isolated local HTTP fixtures; never connects to or changes inspection data.
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Management;

[InitializeOnLoad]
public static class InspectionNavigationChecks
{
    private const string Flag = "arwork.navigation-check";
    static InspectionNavigationChecks()
    {
        EditorApplication.playModeStateChanged += state => {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Flag, false))
                new GameObject("Navigation checks").AddComponent<InspectionNavigationRunner>();
        };
    }

    public static void Run()
    {
        var queuePath = Path.Combine(Application.persistentDataPath, "report-queue-v1");
        if (Directory.Exists(queuePath)) Directory.Move(queuePath, queuePath + ".navigation-backup");
        foreach (var key in new[] { "arwork.server", "arwork.pending-report" })
        {
            SessionState.SetBool(key + ".exists", PlayerPrefs.HasKey(key));
            SessionState.SetString(key + ".backup", PlayerPrefs.GetString(key));
        }
        PlayerPrefs.SetString("arwork.server", "http://127.0.0.1:18081");
        PlayerPrefs.DeleteKey("arwork.pending-report");
        SessionState.SetBool(Flag, true);
        EditorSceneManager.OpenScene("Assets/Inspection.unity");
        EditorApplication.EnterPlaymode();
    }

    internal static void Finish(int code)
    {
        foreach (var key in new[] { "arwork.server", "arwork.pending-report" })
            if (SessionState.GetBool(key + ".exists", false)) PlayerPrefs.SetString(key, SessionState.GetString(key + ".backup", ""));
            else PlayerPrefs.DeleteKey(key);
        var queuePath = Path.Combine(Application.persistentDataPath, "report-queue-v1");
        if (Directory.Exists(queuePath)) Directory.Delete(queuePath, true);
        if (Directory.Exists(queuePath + ".navigation-backup")) Directory.Move(queuePath + ".navigation-backup", queuePath);
        PlayerPrefs.Save(); SessionState.SetBool(Flag, false);
        EditorApplication.Exit(code);
    }
}

public sealed class InspectionNavigationRunner : MonoBehaviour
{
    private HttpListener fixture;
    private int checks;
    private volatile int responseMode;
    private volatile int lastPostPort;
    private string lastPostBody, lastPostKey;
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    private static object Call(object obj, string name) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, null);
    private void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Debug.Log("ISSUE11_PASS=" + label); }

    private IEnumerator Start()
    {
        var flow = Verify();
        while (true)
        {
            bool next;
            try { next = flow.MoveNext(); }
            catch (Exception error) { Debug.LogException(error); fixture?.Close(); InspectionNavigationChecks.Finish(1); yield break; }
            if (!next) break;
            yield return flow.Current;
        }
        fixture.Close(); Debug.Log("ISSUE11_CHECKS_PASSED=" + checks); InspectionNavigationChecks.Finish(0);
    }

    private IEnumerator Verify()
    {
        fixture = new HttpListener();
        fixture.Prefixes.Add("http://127.0.0.1:18081/"); fixture.Prefixes.Add("http://127.0.0.1:18082/"); fixture.Start();
        _ = Task.Run(async () => {
            try
            {
                while (fixture.IsListening)
                {
                    var context = await fixture.GetContextAsync();
                    var request = context.Request; string source = request.Url.Port == 18081 ? "A" : "B";
                    string issue = "{\"id\":\"same-id\",\"title\":\"Server " + source + "\",\"description\":\"fixture\",\"priority\":\"medium\",\"status\":\"in_progress\",\"position\":{\"x\":1,\"y\":2,\"z\":3}}";
                    if (source == "A" || (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/api/reports")) issue = issue.Substring(0, issue.Length - 1) + ",\"snapshotUrl\":\"/api/issues/same-id/snapshot\"}";
                    if (request.Url.AbsolutePath.EndsWith("/snapshot")) {
                        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAwCAIAAAD/zu84AAAAL0lEQVR4nO3NQQkAAAgEsMtkHNMZ1RQ+hMH+S02fikAgEAgEAoFAIBAIBAKB4FOwoW0IakT/d2gAAAAASUVORK5CYII=");
                        context.Response.ContentType = "image/png";
                        context.Response.OutputStream.Write(photo, 0, photo.Length); context.Response.Close(); continue;
                    }
                    string body = request.Url.AbsolutePath == "/api/issues" && request.HttpMethod == "GET" ? "{\"data\":[" + issue + "]}" : "{\"data\":" + issue + "}";
                    if (request.HttpMethod == "POST")
                    {
                        lastPostBody = new StreamReader(request.InputStream).ReadToEnd();
                        lastPostKey = request.Headers["Idempotency-Key"]; lastPostPort = request.Url.Port;
                    }
                    if (request.Url.AbsolutePath == "/health") body = "{\"data\":{\"status\":\"ok\"}}";
                    if (responseMode == 1 && request.Url.AbsolutePath == "/api/issues") body = "{\"data\":[]}";
                    if (responseMode == 2) context.Response.StatusCode = 500;
                    if (request.Url.AbsolutePath == "/api/events") context.Response.StatusCode = 404;
                    var bytes = Encoding.UTF8.GetBytes(body); context.Response.ContentType = "application/json";
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length); context.Response.Close();
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        });
        yield return new WaitForSecondsRealtime(3);
        var probe = FindObjectOfType<ArProbe>(); var form = probe.GetComponent<IssueReportForm>(); var history = probe.GetComponent<IssueHistoryPanel>();
        Check(!probe.CameraActive && (XRGeneralSettings.Instance == null || !XRGeneralSettings.Instance.Manager.isInitializationComplete), "startup leaves XR uninitialized");
        Check(history.IsOpen && !Field<GameObject>(form, "reportPanel").activeSelf, "startup opens records, not form");
        Check(Field<GameObject>(form,"settingsPanel").transform.Find("深度辅助：关")==null
            && Field<GameObject>(form,"settingsPanel").transform.Find("深度辅助：开")==null,"obsolete plane/depth assistance switch is removed");
        Check(Field<GameObject>(probe,"originRoot").GetComponent<UnityEngine.XR.ARFoundation.ARPlaneManager>()==null,
            "AR origin has no plane detection manager");
        Check(!Field<UnityEngine.XR.ARFoundation.AROcclusionManager>(probe,"occlusion").enabled,"idle app does not collect depth");
        var rows = Field<RectTransform>(history, "rows");
        Check(rows.childCount == 1 && rows.GetComponentInChildren<Text>().text.Contains("Server A"), "homepage reads HTTP records");
        var homePhoto = rows.GetComponentInChildren<RawImage>();
        Check(homePhoto != null && homePhoto.texture != null && homePhoto.texture.width == 32 && homePhoto.texture.height == 48,
            "homepage displays associated snapshot thumbnail without camera");
        rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<InputField>(history, "title").text == "Server A", "detail loads without camera");
        Check(Field<Text>(history, "metadata").text.Contains("最近同步："), "online detail shows last successful sync time");
        responseMode = 2; Field<Button>(history, "refresh").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(!Field<InputField>(history, "title").interactable && !Field<Button>(history, "save").interactable &&
            !Field<Button>(history, "remark").interactable && Field<Text>(history, "message").text.Contains("只读"),
            "failed online refresh makes synced detail read only without discarding content");
        responseMode = 0; Field<Button>(history, "refresh").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<InputField>(history, "title").interactable, "successful refresh restores online editing");
        responseMode = 2; yield return new WaitForSecondsRealtime(4);
        Check(!Field<InputField>(history, "title").interactable && Field<Text>(history, "metadata").text.Contains("旧状态"),
            "lost realtime connection reconciles HTTP and locks offline detail without manual refresh");
        responseMode = 0; yield return new WaitForSecondsRealtime(3);
        Check(Field<InputField>(history, "title").interactable, "HTTP recovery restores editing even when SSE is unavailable");
        yield return new WaitForSecondsRealtime(1);
        var photoArea = GameObject.Find("现场照片");
        var photoImage = photoArea.GetComponentInChildren<RawImage>();
        Check(photoImage.texture != null && photoImage.texture.width == 32 && photoImage.texture.height == 48, "detail downloads associated photo with real HTTP");
        photoArea.transform.Find("查看大图").GetComponent<Button>().onClick.Invoke();
        var photoViewer = GameObject.Find("现场照片大图");
        Check(photoViewer != null && photoViewer.GetComponentInChildren<RawImage>().texture != null && !probe.CameraActive, "photo opens full size without starting camera");
        photoViewer.transform.Find("关闭大图").GetComponent<Button>().onClick.Invoke();
        Check(!photoViewer.activeSelf, "photo viewer closes back to record");
        responseMode = 2; Field<Button>(history, "back").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(rows.GetComponentInChildren<RawImage>()?.texture != null,
            "returning to retained list keeps thumbnails when offline refresh fails");
        responseMode = 0; rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Field<InputField>(history, "title").text = "unsaved draft A";
        history.Hide(); form.ShowHome(); yield return new WaitForSecondsRealtime(1);
        rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<InputField>(history, "title").text == "unsaved draft A", "navigation preserves content draft");
        history.Hide(); Field<InputField>(form, "address").text = "http://127.0.0.1:18082";
        Check((bool)Call(form, "SaveAddress"), "settings accepts new service"); form.ShowHome();
        Check(rows.GetComponentsInChildren<Button>().Length == 0, "service switch immediately hides previous rows");
        yield return new WaitForSecondsRealtime(1);
        Check(rows.GetComponentInChildren<Text>().text.Contains("Server B"), "new service reads its own records");
        rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<InputField>(history, "title").text == "Server B", "same ID on another service does not inherit draft");
        Check(GameObject.Find("现场照片").GetComponentInChildren<RawImage>().texture == null, "legacy record does not show previous server photo");
        Check(!probe.CameraActive, "all browsing leaves camera stopped");
        history.Hide(); Field<InputField>(form, "address").text = "file:///invalid";
        Check(!(bool)Call(form, "SaveAddress"), "invalid service is rejected");
        Check(PlayerPrefs.GetString("arwork.server") == "http://127.0.0.1:18082", "invalid input preserves saved service");
        Field<InputField>(form, "address").text = "http://127.0.0.1:18082";
        Field<Button>(form, "test").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<Text>(form, "settingsMessage").text == "连接成功。", "settings tests real health endpoint");
        responseMode = 1; form.ShowHome(); yield return new WaitForSecondsRealtime(1);
        Check(rows.childCount == 0 && Field<Text>(history, "message").text.Contains("暂无服务器问题"), "empty list gives actionable feedback");
        responseMode = 2; Field<Button>(history, "refresh").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<Text>(history, "message").text.Contains("失败"), "HTTP failure gives retry feedback");
        responseMode = 0; Field<Button>(history, "refresh").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(rows.childCount == 1, "refresh recovers records after failure");
        rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Call(history, "BeginRemark");
        Check(probe.CameraActive && history.IsRemarking, "remark explicitly enters camera");
        Field<Button>(history, "cancelRemark").onClick.Invoke();
        Check(!probe.CameraActive && !history.IsRemarking && history.IsOpen, "cancel remark stops camera and returns detail");
        form.ShowHome(); history.Hide();
        Check(!history.IsOpen && !history.Busy, "navigation can cancel loading");
        var local = form.GetComponent<LocalReports>();
        local.enabled = false;
        var localEntry = local.Add("http://127.0.0.1:18082", "/api/reports",
            "{\"title\":\"offline local\",\"description\":\"kept\",\"priority\":\"medium\",\"position\":{\"x\":0,\"y\":1,\"z\":2},\"snapshot\":\"iVBORw0KGgoAAAANSUhEUgAAACAAAAAwCAIAAAD/zu84AAAAL0lEQVR4nO3NQQkAAAgEsMtkHNMZ1RQ+hMH+S02fikAgEAgEAoFAIBAIBAKB4FOwoW0IakT/d2gAAAAASUVORK5CYII=\"}");
        form.ShowHome(); history.RefreshLocalRows();
        Check(Array.TrueForAll(rows.GetComponentsInChildren<Button>(), button => !button.interactable), "queue refresh preserves in-flight list navigation lock");
        yield return new WaitForSecondsRealtime(1);
        var localButton = Array.Find(rows.GetComponentsInChildren<Button>(), button => button.GetComponentInChildren<Text>().text.Contains("offline local"));
        Check(localButton != null, "home includes durable local record");
        Check(localButton.GetComponentInChildren<RawImage>()?.texture != null && localButton.GetComponentInChildren<Text>().text.Contains("待处理"),
            "local home row shows snapshot and provisional processing status");
        localButton.onClick.Invoke();
        Check(local.IsOpen && Field<InputField>(local, "title").interactable && !probe.CameraActive, "unsent local detail opens editable without camera");
        yield return null;
        Canvas.ForceUpdateCanvases();
        var footer = (RectTransform)Field<GameObject>(form, "homeControls").transform.Find("新增问题");
        var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) {
            position = RectTransformUtility.WorldToScreenPoint(null, footer.TransformPoint(footer.rect.center))
        };
        var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        UnityEngine.EventSystems.EventSystem.current.RaycastAll(pointer, hits);
        Debug.Log("ISSUE15_RAYCAST=" + pointer.position + " hits=" + string.Join(",", hits.ConvertAll(hit => hit.gameObject.name)));
        Check(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(Field<GameObject>(local, "panel").transform), "local detail blocks hidden homepage navigation");
        Check(Field<RawImage>(local, "photo").texture.width == 32, "local detail reads persisted snapshot");
        Field<InputField>(local, "title").text = "edited offline";
        Field<Button>(local, "save").onClick.Invoke();
        yield return new WaitForSecondsRealtime(1);
        var localQueue = new ReportQueue(Path.Combine(Application.persistentDataPath, "report-queue-v1"));
        Check(JsonUtility.FromJson<ReportPayload>(localQueue.Get(localEntry.key).body).title == "edited offline", "local edit persists through public queue");
        localQueue.BeginSend(localEntry.key); history.RefreshLocalRows(); yield return null;
        localButton = Array.Find(rows.GetComponentsInChildren<Button>(), button => button.GetComponentInChildren<Text>().text.Contains("edited offline"));
        localButton.onClick.Invoke();
        Check(!Field<InputField>(local, "title").interactable && !Field<Button>(local, "save").interactable, "started local report is read only");
        local.enabled = true;
        yield return new WaitForSecondsRealtime(3);
        Check(local.IsOpen && localQueue.Get(localEntry.key).state == "synced", "read-only local detail does not block automatic upload");
        Check(Field<Text>(local, "info").text.Contains("最近同步：") && !Field<Text>(local, "info").text.Contains("未知") &&
            Field<Text>(local, "info").text.Contains("回执状态：处理中"),
            "acknowledged local detail displays receipt status and durable sync time");
        local.Hide(); history.Hide();
        const string frozen = "{\"title\":\"pending A\",\"description\":\"original\",\"priority\":\"medium\",\"position\":{\"x\":1,\"y\":2,\"z\":3}}";
        var pending = "{\"key\":\"frozen-key\",\"server\":\"http://127.0.0.1:18081\",\"body\":\"" + frozen.Replace("\"", "\\\"") + "\"}";
        PlayerPrefs.SetString("arwork.pending-report", pending); Call(form, "RestorePending");
        var queue = new ReportQueue(Path.Combine(Application.persistentDataPath, "report-queue-v1"));
        var migrated = queue.Get("frozen-key");
        Check(migrated.body == frozen && migrated.frozen && migrated.server == "http://127.0.0.1:18081", "legacy pending migrates with frozen body and original server");
        Check(PlayerPrefs.GetString("arwork.server") == "http://127.0.0.1:18082", "migration does not switch active service");
        Check(!PlayerPrefs.HasKey("arwork.pending-report"), "durable migration clears legacy preference");
        form.ShowHome(); yield return new WaitForSecondsRealtime(1);
        Check(lastPostPort == 18082, "another service never uploads migrated request");

    }
}

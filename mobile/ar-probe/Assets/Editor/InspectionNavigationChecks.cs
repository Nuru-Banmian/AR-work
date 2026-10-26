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
        SessionState.SetBool("depth.exists", PlayerPrefs.HasKey(ArProbe.DepthPreference));
        SessionState.SetInt("depth.backup", PlayerPrefs.GetInt(ArProbe.DepthPreference, 0));
        PlayerPrefs.DeleteKey(ArProbe.DepthPreference);
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
        if (SessionState.GetBool("depth.exists", false)) PlayerPrefs.SetInt(ArProbe.DepthPreference, SessionState.GetInt("depth.backup", 0));
        else PlayerPrefs.DeleteKey(ArProbe.DepthPreference);
        foreach (var key in new[] { "arwork.server", "arwork.pending-report" })
            if (SessionState.GetBool(key + ".exists", false)) PlayerPrefs.SetString(key, SessionState.GetString(key + ".backup", ""));
            else PlayerPrefs.DeleteKey(key);
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
                    if (source == "A") issue = issue.Substring(0, issue.Length - 1) + ",\"snapshotUrl\":\"/api/issues/same-id/snapshot\"}";
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
        var depthButton = Field<GameObject>(form, "settingsPanel").transform.Find("深度辅助：关").GetComponent<Button>();
        Check(!probe.DepthAssistanceEnabled, "depth defaults off");
        depthButton.onClick.Invoke();
        Check(probe.DepthAssistanceEnabled && PlayerPrefs.GetInt(ArProbe.DepthPreference) == 1, "settings depth switch persists enabled choice");
        Check(!probe.CameraActive && !Field<UnityEngine.XR.ARFoundation.AROcclusionManager>(probe, "occlusion").enabled,
            "enabling depth in settings does not start camera or depth capture");
        depthButton.onClick.Invoke();
        Check(!probe.DepthAssistanceEnabled && PlayerPrefs.GetInt(ArProbe.DepthPreference) == 0, "settings depth switch persists disabled choice");
        var rows = Field<RectTransform>(history, "rows");
        Check(rows.childCount == 1 && rows.GetComponentInChildren<Text>().text.Contains("Server A"), "homepage reads HTTP records");
        rows.GetComponentInChildren<Button>().onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(Field<InputField>(history, "title").text == "Server A", "detail loads without camera");
        yield return new WaitForSecondsRealtime(1);
        var photoArea = GameObject.Find("现场照片");
        var photoImage = photoArea.GetComponentInChildren<RawImage>();
        Check(photoImage.texture != null && photoImage.texture.width == 32 && photoImage.texture.height == 48, "detail downloads associated photo with real HTTP");
        photoArea.transform.Find("查看大图").GetComponent<Button>().onClick.Invoke();
        var photoViewer = GameObject.Find("现场照片大图");
        Check(photoViewer != null && photoViewer.GetComponentInChildren<RawImage>().texture != null && !probe.CameraActive, "photo opens full size without starting camera");
        photoViewer.transform.Find("关闭大图").GetComponent<Button>().onClick.Invoke();
        Check(!photoViewer.activeSelf, "photo viewer closes back to record");
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
        Check(rows.childCount == 0 && Field<Text>(history, "message").text.Contains("暂无问题"), "empty list gives actionable feedback");
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
        const string frozen = "{\"title\":\"pending A\",\"description\":\"original\",\"priority\":\"medium\",\"position\":{\"x\":1,\"y\":2,\"z\":3}}";
        var pending = "{\"key\":\"frozen-key\",\"server\":\"http://127.0.0.1:18081\",\"body\":\"" + frozen.Replace("\"", "\\\"") + "\"}";
        PlayerPrefs.SetString("arwork.pending-report", pending); Call(form, "RestorePending");
        Check(Field<InputField>(form, "title").text == "pending A", "legacy pending report restores its content");
        Check(PlayerPrefs.GetString("arwork.server") == "http://127.0.0.1:18082", "pending restore does not switch active service");
        Call(form, "BeginReport");
        Check(!probe.CameraActive && Field<GameObject>(form, "reportPanel").activeSelf, "pending retry opens form without camera");
        Field<Button>(form, "submit").onClick.Invoke(); yield return new WaitForSecondsRealtime(1);
        Check(lastPostPort == 18081 && lastPostKey == "frozen-key" && lastPostBody == frozen, "retry preserves original server key and exact body");
        Check(!PlayerPrefs.HasKey("arwork.pending-report"), "confirmed receipt clears pending request");
    }
}

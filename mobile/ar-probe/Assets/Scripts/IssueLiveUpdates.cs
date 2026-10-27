using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// SSE only invalidates snapshots. Network reads/cancellation never run on Unity's
// main thread, including while Android is pausing its native download callbacks.
public sealed class IssueLiveUpdates : MonoBehaviour
{
    [Serializable] private sealed class IssueList { public IssueHistoryPanel.Issue[] data; public string[] deletedIds; }

    private sealed class EventConnection
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private int ready, dirty, done;
        private long lastReceived = System.Diagnostics.Stopwatch.GetTimestamp();
        public bool TakeReady() => Interlocked.Exchange(ref ready, 0) != 0;
        public bool TakeDirty() => Interlocked.Exchange(ref dirty, 0) != 0;
        public bool Done => Volatile.Read(ref done) != 0;
        public double SilenceSeconds => (System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReceived)) /
            (double)System.Diagnostics.Stopwatch.Frequency;

        public EventConnection(string address) { _ = Task.Run(() => Read(address)); }

        private async Task Read(string address)
        {
            try
            {
                using (var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var response = await client.GetAsync(address + "/api/events",
                    HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") return;
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (cancellation.Token.Register(() => body.Close()))
                    using (var reader = new StreamReader(body))
                    {
                        while (!cancellation.IsCancellationRequested)
                        {
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) break;
                            Interlocked.Exchange(ref lastReceived, System.Diagnostics.Stopwatch.GetTimestamp());
                            if (line == "event: ready") { Interlocked.Exchange(ref ready, 1); Interlocked.Exchange(ref dirty, 1); }
                            else if (line == "event: issues-changed") Interlocked.Exchange(ref dirty, 1);
                        }
                    }
                }
            }
            catch (HttpRequestException) { }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { Volatile.Write(ref done, 1); cancellation.Dispose(); }
        }

        public void Stop()
        {
            // Cancel can close a stream synchronously. Keep that work off the
            // Android pause callback; the reader owns disposal in its finally.
            _ = Task.Run(() => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } });
        }
    }

    private string server;
    private int generation;
    private bool paused, dirty, fetching, connected;
    private float retryAt, reconnectAt;
    private EventConnection connection;
    private IssueHistoryPanel history;
    private ArProbe probe;
    private Text label;
    private string state = "正在连接…";

    public void Initialize(Transform parent, ArProbe owner, IssueHistoryPanel panel)
    {
        probe = owner; history = panel;
        label = IssueReportForm.Label(parent, 23);
        label.rectTransform.anchorMin = new Vector2(0, 1); label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.pivot = new Vector2(0.5f, 1);
        label.rectTransform.sizeDelta = new Vector2(-40, 100);
        label.rectTransform.anchoredPosition = new Vector2(0, -10);
        SetState(state);
    }

    public void ShowLabel(bool visible) { if (label != null) label.gameObject.SetActive(visible); }

    private void SetState(string value)
    {
        state = value;
        if (label != null) label.text = "同步：" + state + "\n橙：待处理  蓝：处理中  绿：已解决  黄：草稿";
    }

    public void SetServer(string value)
    {
        if (server == value) return;
        StopConnection(); server = value; generation++; Refresh();
    }

    public void Refresh() { dirty = true; retryAt = 0; }

    private void StopConnection()
    {
        var previous = connection; connection = null; connected = false; reconnectAt = 0;
        previous?.Stop();
    }

    private void Update()
    {
        if (paused || string.IsNullOrEmpty(server)) return;
        if (connection == null && Time.realtimeSinceStartup >= reconnectAt)
        { SetState("正在连接…"); connection = new EventConnection(server); }
        if (connection != null)
        {
            if (connection.TakeReady()) { connected = true; SetState("已连接"); Refresh(); }
            if (connection.TakeDirty()) Refresh();
            if (connection.Done || connection.SilenceSeconds > 25)
            {
                StopConnection(); reconnectAt = Time.realtimeSinceStartup + 2f;
                Refresh(); // An SSE-only failure may still permit HTTP; reconcile before declaring offline.
                SetState("连接中断，正在重连，可手动刷新");
            }
        }
        if (dirty && !fetching && history.CanApplyLive && Time.realtimeSinceStartup >= retryAt)
            StartCoroutine(Query());
    }

    private IEnumerator Query()
    {
        fetching = true; dirty = false;
        int started = generation; string address = server;
        using (var request = UnityWebRequest.Get(address + "/api/issues"))
        {
            request.timeout = 15; history.GuardRead(request);
            yield return request.SendWebRequest();
            if (started == generation && !paused)
            {
                bool applied = false;
                if (request.result == UnityWebRequest.Result.Success && history.AcceptRead(request) && history.CanApplyLive)
                {
                    try
                    {
                        var snapshot = JsonUtility.FromJson<IssueList>(request.downloadHandler.text);
                        var issues = snapshot?.data;
                        if (issues == null) throw new ArgumentException("Missing issues");
                        history.ApplyLiveIssues(address, issues, history.VerifiedRead(request) ? snapshot.deletedIds : null);
                        foreach (var issue in issues) probe.UpdateMarkerStatus(address, issue.id, issue.status);
                        SetState(connected ? "已连接" : "连接中断，正在重连，可手动刷新");
                        applied = true;
                    }
                    catch (Exception error) { Debug.LogWarning("问题同步或本地清理未完成：" + error.Message); }
                }
                if (!applied) { history.MarkSyncUnavailable(address); dirty = true; retryAt = Time.realtimeSinceStartup + 2f; SetState("同步未完成，旧状态请核对最近同步时间，正在重试"); }
            }
        }
        fetching = false;
    }

    private void OnApplicationPause(bool value)
    {
        paused = value; generation++; StopConnection();
        if (value) SetState("已暂停"); else Refresh();
        Debug.Log("ARWORK_LIVE_PAUSED=" + value);
    }

    private void OnDestroy() { StopConnection(); }
}

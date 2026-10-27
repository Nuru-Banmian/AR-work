using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class ReportQueueSender
{
    [Serializable] private sealed class Result { public IssueHistoryPanel.Issue data; }
    [Serializable] private sealed class Failure { public DeletedError error; }
    [Serializable] private sealed class DeletedError { public string code, issueId; }
    private readonly ReportQueue queue;
    private readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
    private string lastAttempt;
    public ReportQueueSender(ReportQueue queue) { this.queue = queue; }

    // Only one request can be in flight, including simultaneous manual/automatic retries.
    public async Task<bool> UploadNext(string server, HttpClient client, string requestedKey = null)
    {
        if (!await sending.WaitAsync(0)) return false;
        try {
            var candidates = queue.List(server).Where(item => item.state != "deleted" && (item.state != "synced" ||
                (requestedKey != null && ReportQueue.IsRemarkEndpoint(item.endpoint))) &&
                (requestedKey == null || item.key == requestedKey)).ToArray();
            var entry = candidates.FirstOrDefault(item => string.CompareOrdinal(item.key, lastAttempt) > 0)
                ?? candidates.FirstOrDefault();
            if (entry == null) return false;
            lastAttempt = entry.key;
            entry = queue.BeginSend(entry.key); // durable before any network I/O
            try {
                queue.Stage(entry.key, "正在核对接收端身份");
                var receiver = await ReceiverDiscovery.Verify(client, server, string.IsNullOrEmpty(entry.receiverId) ? queue.ReceiverAt(server) : entry.receiverId);
                entry = queue.BindReceiver(entry.key, server, receiver.id);
                queue.RememberLegacyReceiver(server, receiver.id);
                using (var request = new HttpRequestMessage(ReportQueue.IsRemarkEndpoint(entry.endpoint) ? new HttpMethod("PATCH") : HttpMethod.Post, server + entry.endpoint)) {
                    request.Headers.Add("X-ARWork-Receiver", receiver.id);
                    request.Headers.Add("Idempotency-Key", entry.key);
                    request.Content = new StringContent(entry.body, Encoding.UTF8, "application/json");
                    queue.Stage(entry.key, "正在传送完整资料 / 等待接收端持久保存回执");
                    using (var response = await client.SendAsync(request)) {
                        var receipt = await response.Content.ReadAsStringAsync();
                        if (response.StatusCode == System.Net.HttpStatusCode.Gone) {
                            var deleted = JsonUtility.FromJson<Failure>(receipt)?.error;
                            if (deleted?.code == "issue_deleted" && !string.IsNullOrEmpty(deleted.issueId) &&
                                (!ReportQueue.IsRemarkEndpoint(entry.endpoint) || entry.endpoint == "/api/issues/" + deleted.issueId + "/position")) {
                                queue.Deleted(entry.key, deleted.issueId); return true;
                            }
                        }
                        response.EnsureSuccessStatusCode();
                        var issue = JsonUtility.FromJson<Result>(receipt)?.data;
                        if (issue == null || string.IsNullOrEmpty(issue.id) || issue.position == null ||
                            (entry.endpoint == "/api/reports" && string.IsNullOrEmpty(issue.snapshotUrl)))
                            throw new InvalidOperationException("服务器回执不完整，保留原请求重试");
                        var payload = JsonUtility.FromJson<ReportPayload>(entry.body);
                        if (payload.reportVersion == 2 && (string.IsNullOrEmpty(issue.modelUrl) || string.IsNullOrEmpty(issue.snapshotUrl)))
                            throw new InvalidOperationException("服务器模型回执不完整，保留原请求重试");
                        if (ReportQueue.IsRemarkEndpoint(entry.endpoint) && entry.endpoint != "/api/issues/" + issue.id + "/position")
                            throw new InvalidOperationException("重新标记回执问题编号不一致，保留原请求");
                        queue.Complete(entry.key, issue.id, receipt);
                    }
                }
            } catch (Exception error) {
                queue.Fail(entry.key, "结果未确认，原请求已保留。" + error.Message);
            }
            return true;
        } finally { sending.Release(); }
    }
}

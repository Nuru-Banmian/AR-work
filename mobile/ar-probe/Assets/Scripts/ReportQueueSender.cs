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
    private readonly ReportQueue queue;
    private readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
    private string lastAttempt;
    public ReportQueueSender(ReportQueue queue) { this.queue = queue; }

    // Only one request can be in flight, including simultaneous manual/automatic retries.
    public async Task<bool> UploadNext(string server, HttpClient client, string requestedKey = null)
    {
        if (!await sending.WaitAsync(0)) return false;
        try {
            var candidates = queue.List(server).Where(item => item.state != "synced" &&
                (requestedKey == null || item.key == requestedKey)).ToArray();
            var entry = candidates.FirstOrDefault(item => string.CompareOrdinal(item.key, lastAttempt) > 0)
                ?? candidates.FirstOrDefault();
            if (entry == null) return false;
            lastAttempt = entry.key;
            entry = queue.BeginSend(entry.key); // durable before any network I/O
            try {
                using (var request = new HttpRequestMessage(HttpMethod.Post, entry.server + entry.endpoint)) {
                    request.Headers.Add("Idempotency-Key", entry.key);
                    request.Content = new StringContent(entry.body, Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request)) {
                        response.EnsureSuccessStatusCode();
                        var receipt = await response.Content.ReadAsStringAsync();
                        var issue = JsonUtility.FromJson<Result>(receipt)?.data;
                        if (issue == null || string.IsNullOrEmpty(issue.id) || issue.position == null ||
                            (entry.endpoint == "/api/reports" && string.IsNullOrEmpty(issue.snapshotUrl)))
                            throw new InvalidOperationException("服务器回执不完整，保留原请求重试");
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

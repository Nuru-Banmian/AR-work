using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class ReportQueueChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "arwork-queue-" + Guid.NewGuid().ToString("N"));
        try {
            var queue = new ReportQueue(root);
            var first = queue.Add("http://localhost:18095", "/api/reports", "{\"title\":\"first\"}");
            queue.Add("http://localhost:18095", "/api/reports", "{\"title\":\"second\"}");
            var restarted = new ReportQueue(root);
            Require(restarted.List("http://localhost:18095").Length == 2, "multiple records survive restart");
            Require(restarted.List("http://localhost:18096").Length == 0, "servers are isolated");
            Require(restarted.List("http://localhost:18095")[0].state == "pending", "local save is pending, not synced");
            restarted.Edit(first.key, "{\"title\":\"edited\"}");
            Require(new ReportQueue(root).Get(first.key).body == "{\"title\":\"edited\"}", "unsent edits survive restart");
            var frozen = restarted.BeginSend(first.key);
            bool blocked = false;
            try { restarted.Edit(first.key, "changed"); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked && new ReportQueue(root).Get(first.key).frozen, "first send durably freezes request");
            restarted.Fail(first.key, "connection lost");
            Require(restarted.BeginSend(first.key).body == frozen.body, "ambiguous retry retains exact body");
            var editable = restarted.Add("http://localhost:18095", "/api/reports", "original");
            Directory.CreateDirectory(Path.Combine(root, editable.key + ".json.tmp"));
            bool writeFailed = false;
            try { restarted.Edit(editable.key, "replacement"); } catch (IOException) { writeFailed = true; }
            catch (UnauthorizedAccessException) { writeFailed = true; }
            Require(writeFailed && restarted.Get(editable.key).body == "original", "failed write preserves durable original");
            Task.Run(() => CheckHttp(Path.Combine(root, "http"))).GetAwaiter().GetResult();
            Debug.Log("ISSUE15_QUEUE_CHECKS_PASSED");
            EditorApplication.Exit(0);
        } catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CheckHttp(string root)
    {
        const string server = "http://127.0.0.1:18096";
        const string photo = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAwCAIAAAD/zu84AAAAL0lEQVR4nO3NQQkAAAgEsMtkHNMZ1RQ+hMH+S02fikAgEAgEAoFAIBAIBAKB4FOwoW0IakT/d2gAAAAASUVORK5CYII=";
        string body = "{\"title\":\"offline first\",\"description\":\"queue acceptance\",\"priority\":\"medium\",\"position\":{\"x\":0,\"y\":1,\"z\":2},\"snapshot\":\"" + photo + "\"}";
        var queue = new ReportQueue(root);
        queue.Add(server, "/api/reports", body);
        queue.Add(server, "/api/reports", body.Replace("offline first", "offline second"));
        queue.Add("http://127.0.0.1:18097", "/api/reports", body);
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }) {
            var sender = new ReportQueueSender(queue);
            await sender.UploadNext(server, client);
            Require(queue.List(server)[0].state == "failed", "lost receipt retains failed request");
            queue = new ReportQueue(root); sender = new ReportQueueSender(queue);
            await Task.WhenAll(sender.UploadNext(server, client), sender.UploadNext(server, client));
            for (int i = 0; i < 4; i++) await sender.UploadNext(server, client);
            var entries = new ReportQueue(root).List(server);
            Require(entries.Length == 2 && Array.TrueForAll(entries, item => item.state == "synced"), "restart retries and acknowledges both records");
            Require(entries[0].serverId != entries[1].serverId, "different local records receive different server IDs");
            Require(queue.List("http://127.0.0.1:18097")[0].state == "pending", "another service is never uploaded");
            var json = await client.GetStringAsync("http://127.0.0.1:18095/api/issues");
            var remote = JsonUtility.FromJson<RemoteList>(json).data;
            Require(remote.Length == 2, "real backend has exactly two reports after response loss and concurrent retry");
            foreach (var item in remote) {
                var image = await client.GetByteArrayAsync("http://127.0.0.1:18095" + item.snapshotUrl);
                Require(Convert.ToBase64String(image) == photo, "server photo matches complete local snapshot");
            }
        }
    }
    [Serializable] private sealed class RemoteList { public IssueHistoryPanel.Issue[] data; }

    private static void Require(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Debug.Log("ISSUE15_PASS=" + label);
    }
}

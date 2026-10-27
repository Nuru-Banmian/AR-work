using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class ReceiverQueueChecks
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "arwork-receivers-" + Guid.NewGuid().ToString("N"));
        try { Task.Run(() => Verify(root)).GetAwaiter().GetResult(); Debug.Log("ISSUE23_RECEIVER_QUEUE_PASSED"); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Debug.Log("ISSUE23_PASS=" + label); }
    private static async Task Verify(string root)
    {
        using (var first = new Fixture(18121, A))
        using (var moved = new Fixture(18122, A))
        using (var other = new Fixture(18123, B))
        using (var client = ReceiverDiscovery.CreateClient(3)) {
            const string body = "{\"title\":\"frozen snapshot\",\"snapshot\":\"original bytes\"}";
            var queue = new ReportQueue(root);
            var legacy = queue.Add(first.Address, "/api/reports", body, "original-key", true);
            var sender = new ReportQueueSender(queue);
            first.Fail = true;
            await sender.UploadNext(first.Address, client);
            Check(queue.Get(legacy.key).state == "failed" && queue.Get(legacy.key).receiverId == A, "legacy binds only after original-address identity handshake and persists failure");
            Check(first.Posts == 1 && first.Body == body && first.Key == legacy.key, "first HTTP request carries exact frozen bytes and key");
            queue = new ReportQueue(root);
            queue.RememberReceiver(moved.Address, A);
            Check(queue.List(moved.Address).Length == 1 && queue.Get(legacy.key).server == first.Address, "restart and identity route change preserve original address and request");
            queue.RememberReceiver(other.Address, B);
            await new ReportQueueSender(queue).UploadNext(other.Address, client);
            Check(other.Posts == 0 && queue.Get(legacy.key).state == "failed", "selecting another receiver cannot forward original queue");
            moved.Identity = B;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(moved.Posts == 0 && queue.Get(legacy.key).state == "failed", "another receiver taking remembered address receives no request body");
            moved.Identity = A;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get(legacy.key).state == "synced" && moved.Posts == 1 && moved.Body == body && moved.Key == legacy.key && moved.Target == A, "same receiver at new address retries exact body and key with receiver guard");
            var current = queue.Add(moved.Address, "/api/reports", body, "new-key");
            Check(current.receiverId == A, "new requests freeze chosen receiver identity before network I/O");
            moved.LegacyService = true;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(moved.Posts == 1 && queue.Get(current.key).state == "failed", "unrelated service at old address cannot receive request");
            queue = new ReportQueue(Path.Combine(root, "unverified"));
            var unknown = queue.Add(other.Address, "/api/issues", "legacy", "legacy-unverified", true);
            other.LegacyService = true;
            await new ReportQueueSender(queue).UploadNext(other.Address, client);
            Check(queue.Get(unknown.key).state == "failed" && string.IsNullOrEmpty(queue.Get(unknown.key).receiverId) && other.Posts == 0, "unverifiable old-address queue stays durable");
            queue = new ReportQueue(Path.Combine(root, "racing-rediscovery"));
            queue.RememberReceiver(first.Address, A);
            queue.Add(first.Address, "/api/reports", body, "racing-key");
            first.HandshakeStarted = new TaskCompletionSource<bool>(); first.ReleaseHandshake = new TaskCompletionSource<bool>();
            var inflight = new ReportQueueSender(queue).UploadNext(first.Address, client);
            await first.HandshakeStarted.Task;
            queue.RememberReceiver(moved.Address, A);
            first.ReleaseHandshake.SetResult(true);
            await inflight;
            Check(queue.ReceiverAt(moved.Address) == A && queue.ReceiverAt(first.Address) == null && queue.List(moved.Address).Length == 1,
                "in-flight old-address handshake cannot roll rediscovered route back");
            var modelBody = "{\"reportVersion\":2,\"model\":{\"version\":1},\"snapshot\":\"frozen photo\"}";
            queue = new ReportQueue(Path.Combine(root, "model-http")); queue.RememberReceiver(moved.Address, A);
            queue.Add(moved.Address, "/api/reports", modelBody, "model-http", true);
            moved.LegacyService = false;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get("model-http").state == "failed", "snapshot-only receipt cannot acknowledge model request");
            moved.IncludeModel = true;
            queue = new ReportQueue(Path.Combine(root, "model-http"));
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get("model-http").state == "synced" && moved.Body == modelBody && moved.Key == "model-http", "restart retries model HTTP bytes and completes only full receipt");
            queue = new ReportQueue(Path.Combine(root, "remark-http")); queue.RememberReceiver(moved.Address, A);
            var scene = JsonUtility.FromJson<IssueScanModel>("{\"version\":1,\"vertices\":[0,0,0,1,0,0,0,1,0],\"colors\":[255,0,0,0,255,0,0,0,255],\"triangles\":[0,1,2],\"marker\":{\"position\":{\"x\":0.25,\"y\":0.25,\"z\":0},\"normal\":{\"x\":0,\"y\":0,\"z\":1}}}");
            var remarkBody = RemarkPayload.Freeze(scene, "confirmed-photo", new Vector3(7,8,9));
            queue.Add(moved.Address, "/api/issues/saved/position", remarkBody, "remark-http", true);
            scene.vertices[0] = 99;
            moved.DropReceipt = true;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get("remark-http").state == "failed" && moved.Method == "PATCH", "lost remark receipt retains original complete PATCH request");
            queue = new ReportQueue(Path.Combine(root, "remark-http"));
            Check(queue.Get("remark-http").body == remarkBody, "restart preserves model photo point and key independently of scan mutation");
            moved.WrongId = true;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get("remark-http").state == "failed", "another issue receipt cannot acknowledge a remark");
            moved.WrongId = false;
            await new ReportQueueSender(queue).UploadNext(moved.Address, client);
            Check(queue.Get("remark-http").state == "synced" && moved.Body == remarkBody && moved.Key == "remark-http", "same frozen replacement is acknowledged after process reconstruction");
            var retained = queue.Add(moved.Address, "/api/reports", body, "unrelated", true);
            queue.ForgetSynced(moved.Address, "saved");
            Check(queue.List(moved.Address).Length == 1 && queue.Get(retained.key).body == body, "retiring old acknowledged copies preserves unrelated pending data");


        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public readonly string Address;
        public volatile string Identity;
        public volatile bool Fail, LegacyService, IncludeModel, DropReceipt, WrongId;
        public volatile int Posts;
        public string Body, Key, Target, Method;
        public TaskCompletionSource<bool> HandshakeStarted, ReleaseHandshake;
        public Fixture(int port, string id)
        {
            Identity = id; Address = "http://127.0.0.1:" + port;
            listener.Prefixes.Add(Address + "/"); listener.Start();
            _ = Task.Run(async () => {
                try {
                    while (listener.IsListening) {
                        var context = await listener.GetContextAsync();
                        var request = context.Request;
                        string response;
                        if (request.HttpMethod == "POST" || request.HttpMethod == "PATCH") {
                            Method = request.HttpMethod;
                            Posts++; Body = new StreamReader(request.InputStream).ReadToEnd(); Key = request.Headers["Idempotency-Key"]; Target = request.Headers["X-ARWork-Receiver"];
                            context.Response.StatusCode = Fail ? 500 : 201;
                            response = "{\"data\":{\"id\":\"saved\",\"position\":{\"x\":0,\"y\":0,\"z\":0},\"snapshotUrl\":\"/snapshot\"}}";
                            if (IncludeModel) response = response.Replace("\"snapshotUrl\"", "\"modelUrl\":\"/model\",\"snapshotUrl\"");
                            if (WrongId) response = response.Replace("saved", "another");
                            if (DropReceipt) { DropReceipt = false; context.Response.Abort(); continue; }
                        } else response = LegacyService ? "{\"data\":{\"status\":\"ok\"}}" : "{\"data\":{\"id\":\"" + Identity + "\",\"name\":\"fixture\",\"protocol\":1,\"port\":" + port + "}}";
                        if (request.Url.AbsolutePath == "/api/receiver" && ReleaseHandshake != null) {
                            HandshakeStarted.SetResult(true); await ReleaseHandshake.Task; ReleaseHandshake = null;
                        }
                        var bytes = Encoding.UTF8.GetBytes(response); context.Response.ContentType = "application/json";
                        context.Response.OutputStream.Write(bytes, 0, bytes.Length); context.Response.Close();
                    }
                } catch (ObjectDisposedException) { } catch (HttpListenerException) { }
            });
        }
        public void Dispose() { listener.Close(); }
    }
}

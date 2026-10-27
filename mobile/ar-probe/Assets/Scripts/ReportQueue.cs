using System;
using System.IO;
using System.Linq;
using UnityEngine;

[Serializable] public sealed class ReportPayload
{
    public string title, description, priority, snapshot;
    public IssueHistoryPanel.Position position;
}

// Each complete request (including photo bytes) is one atomic durable document.
// Callers receive copies: edits cannot mutate a frozen request in memory.
public sealed class ReportQueue
{
    [Serializable] public sealed class Entry
    {
        public int version = 1;
        public string key, server, endpoint, body, state = "pending", serverId, error, receipt;
        public string syncedAt, receiverId;
        public bool frozen;
    }

    private readonly string root;
    public ReportQueue(string directory) { root = directory; Directory.CreateDirectory(root); }

    private string RoutesPath => Path.Combine(root, "routes", "receivers.json");
    [Serializable] private sealed class Routes { public Route[] items = new Route[0]; }
    [Serializable] private sealed class Route { public string address, id; }
    private Routes ReadRoutes() => File.Exists(RoutesPath)
        ? JsonUtility.FromJson<Routes>(File.ReadAllText(RoutesPath)) : new Routes();
    public string ReceiverAt(string address) => ReadRoutes().items.FirstOrDefault(item => item.address == address)?.id;

    public void RememberReceiver(string address, string id)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Missing receiver identity");
        var routes = ReadRoutes();
        routes.items = routes.items.Where(item => item.address != address && item.id != id)
            .Concat(new[] { new Route { address = address, id = id } }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(RoutesPath));
        AtomicWrite(RoutesPath, JsonUtility.ToJson(routes));
    }

    public void RememberLegacyReceiver(string address, string id)
    {
        // A handshake that started before rediscovery must not roll a newer route back.
        var routes = ReadRoutes();
        if (routes.items.Any(item => item.id == id || item.address == address)) return;
        RememberReceiver(address, id);
    }

    public Entry[] List(string server)
    {
        var id = ReceiverAt(server);
        return Directory.GetFiles(root, "*.json").Select(Read)
            .Where(entry => string.IsNullOrEmpty(entry.receiverId) ? entry.server == server : entry.receiverId == id)
            .OrderBy(entry => entry.key).ToArray();
    }

    // A legacy entry can bind only at its original address after a successful handshake.
    public Entry BindReceiver(string key, string address, string id)
    {
        var entry = Get(key);
        if (string.IsNullOrEmpty(entry.receiverId) ? entry.server != address : entry.receiverId != id)
            throw new InvalidOperationException("Receiver mismatch; frozen request retained");
        entry.receiverId = id;
        Write(entry); return Get(key);
    }

    public Entry Get(string key) => Read(PathFor(key));

    public Entry Add(string server, string endpoint, string body, string key = null, bool frozen = false, string receiverIdentity = null)
    {
        var entry = new Entry { key = key ?? Guid.NewGuid().ToString("N"), server = server,
            endpoint = endpoint, body = body, frozen = frozen, receiverId = receiverIdentity ?? ReceiverAt(server) };
        Validate(entry);
        if (File.Exists(PathFor(entry.key))) {
            var existing = Get(entry.key);
            if (existing.server != server || existing.endpoint != endpoint || existing.body != body)
                throw new InvalidDataException("Pending request conflicts with queue");
            return existing;
        }
        Write(entry); return Get(entry.key);
    }

    public void Edit(string key, string body)
    {
        var entry = Get(key);
        if (entry.frozen) throw new InvalidOperationException("This request has already started sending");
        entry.body = body; Validate(entry); Write(entry);
    }

    public Entry BeginSend(string key)
    {
        var entry = Get(key);
        if (entry.state == "synced") throw new InvalidOperationException("Already acknowledged");
        entry.frozen = true; entry.state = "uploading"; entry.error = null;
        Write(entry); return Get(key);
    }

    public void Fail(string key, string error)
    {
        var entry = Get(key);
        if (entry.state == "synced") return;
        entry.state = "failed"; entry.error = error; Write(entry);
    }

    public void Complete(string key, string serverId, string receipt)
    {
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(receipt)) throw new ArgumentException("Missing receipt");
        var entry = Get(key);
        entry.serverId = serverId; entry.receipt = receipt; entry.state = "synced"; entry.error = null;
        entry.syncedAt = DateTimeOffset.UtcNow.ToString("O");
        Write(entry);
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 200 || key.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException("Invalid queue key");
        return Path.Combine(root, key + ".json");
    }

    private Entry Read(string path)
    {
        var entry = JsonUtility.FromJson<Entry>(File.ReadAllText(path));
        Validate(entry);
        if (PathFor(entry.key) != path) throw new InvalidDataException("Queue identity mismatch");
        return entry;
    }

    private static void Validate(Entry entry)
    {
        if (entry == null || entry.version != 1 || string.IsNullOrEmpty(entry.body) ||
            !Uri.TryCreate(entry.server, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (entry.endpoint != "/api/reports" && entry.endpoint != "/api/issues") ||
            (entry.state != "pending" && entry.state != "uploading" && entry.state != "failed" && entry.state != "synced") ||
            (entry.state == "synced" && (string.IsNullOrEmpty(entry.serverId) || string.IsNullOrEmpty(entry.receipt))))
            throw new InvalidDataException("Invalid local report; preserve application data");
    }

    private void Write(Entry entry)
    {
        AtomicWrite(PathFor(entry.key), JsonUtility.ToJson(entry));
    }

    private static void AtomicWrite(string path, string json)
    {
        var temporary = path + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) {
                writer.Write(json); writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public sealed class ReferenceTarget
{
    public string id, name, location, imageBase64, imageSha256;
    public int version;
    public float widthMeters, heightMeters;
    public bool active;
    public string ImageName => id + "-v" + version;
    public string Caption => name + " v" + version + " · " + location;
    public bool Matches(CardReference point) => point != null && active && point.cardId == id && point.version == version &&
        Mathf.Abs(point.widthMeters - widthMeters) < 0.000001f && Mathf.Abs(point.heightMeters - heightMeters) < 0.000001f;
    public Texture2D DecodeImage()
    {
        var bytes = Convert.FromBase64String(imageBase64);
        if (bytes.Length > 512 * 1024 || ReferenceCatalog.Hash(bytes) != imageSha256) throw new ArgumentException("Reference image hash mismatch");
        var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!texture.LoadImage(bytes) || texture.width < 256 || texture.height < 256 || texture.width > 2048 || texture.height > 2048)
        { UnityEngine.Object.Destroy(texture); throw new ArgumentException("Invalid reference image"); }
        return texture;
    }
}

// Only downloaded reference data is cached here; issue/media offline history is #14.
public sealed class ReferenceCatalog : MonoBehaviour
{
    [Serializable] public sealed class TargetList { public ReferenceTarget[] data; }
    public ReferenceTarget[] Targets { get; private set; } = new ReferenceTarget[0];
    public string Server { get; private set; }
    public string Message { get; private set; }
    public bool Busy { get; private set; }
    public ReferenceTarget Selected { get; private set; }
    public static string Hash(byte[] bytes)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private string Key => "arwork.reference." + Hash(Encoding.UTF8.GetBytes(Server));
    private string CachePath => Path.Combine(Application.persistentDataPath, Key + ".json");

    public void SetServer(string server)
    {
        if (Server == server) return;
        Server = server; Selected = null; Targets = new ReferenceTarget[0];
        try { if (File.Exists(CachePath)) Apply(JsonUtility.FromJson<TargetList>(File.ReadAllText(CachePath))); }
        catch (Exception e) { Message = "本机参照资料不可用，请联网刷新。"; Debug.LogException(e); }
    }
    private void Apply(TargetList list)
    {
        if (list?.data == null) throw new ArgumentException("Missing references");
        foreach (var target in list.data)
        {
            if (target == null || string.IsNullOrEmpty(target.id) || target.version < 1 || string.IsNullOrEmpty(target.name) ||
                target.widthMeters < 0.05f || target.heightMeters < 0.05f || target.widthMeters > 5 || target.heightMeters > 5)
                throw new ArgumentException("Invalid reference metadata");
            // Validate downloaded bytes without allocating every target as a GPU texture
            // in a single frame. Decode only the selected or requested image on demand.
            var bytes = Convert.FromBase64String(target.imageBase64);
            if (bytes.Length > 512 * 1024 || Hash(bytes) != target.imageSha256)
                throw new ArgumentException("Reference image hash mismatch");
        }
        Targets = list.data;
        string selected = PlayerPrefs.GetString(Key, "");
        Selected = Array.Find(Targets, t => t.active && t.ImageName == selected);
        if (Selected == null) Selected = Array.Find(Targets, t => t.active);
    }
    public void Select(ReferenceTarget target)
    {
        if (target == null || !target.active) return;
        Selected = target; PlayerPrefs.SetString(Key, target.ImageName); PlayerPrefs.Save();
    }
    public ReferenceTarget Find(CardReference point) => Array.Find(Targets, t => t.Matches(point));
    public IEnumerator Refresh()
    {
        if (Busy || string.IsNullOrEmpty(Server)) yield break;
        Busy = true; Message = "正在下载参照资料…";
        string requestedServer = Server;
        using (var request = UnityWebRequest.Get(Server + "/api/references"))
        {
            request.timeout = 20;
            yield return request.SendWebRequest();
            if (requestedServer == Server)
            {
                if (request.result != UnityWebRequest.Result.Success)
                    Message = Targets.Length == 0 ? "无法获取参照，请联网刷新或登记。" : "离线使用已下载参照；无法得知远端是否已停用，请确认现场未改变。";
                else try
                {
                    var list = JsonUtility.FromJson<TargetList>(request.downloadHandler.text);
                    Apply(list);
                    var temp = CachePath + ".tmp";
                    File.WriteAllText(temp, JsonUtility.ToJson(list));
                    if (File.Exists(CachePath)) File.Replace(temp, CachePath, null); else File.Move(temp, CachePath);
                    Message = Selected == null ? "暂无可用参照，请先登记固定标牌或海报。" : "参照已下载，可离线识别。";
                }
                catch (Exception e) { Message = "参照资料读取或本机保存失败，请刷新重试。"; Debug.LogException(e); }
            }
        }
        Busy = false;
    }
}

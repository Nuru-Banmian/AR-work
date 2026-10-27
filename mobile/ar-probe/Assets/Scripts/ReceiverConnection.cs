using System;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

[Serializable] public sealed class ReceiverInfo
{
    public string id, name;
    public int protocol, port;
    [NonSerialized] public string address;
    public bool Valid => protocol == 1 && id != null && id.Length == 32 &&
        Array.TrueForAll(id.ToCharArray(), c => "0123456789abcdef".IndexOf(c) >= 0) &&
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && port > 0 && port <= 65535;
}

public static class ReceiverConnection
{
    public static HttpClient CreateClient(int timeoutSeconds) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    [Serializable] private sealed class Envelope { public ReceiverInfo data; }
    public static async Task<ReceiverInfo> Verify(HttpClient client, string address, string expected = null)
    {
        var json = await client.GetStringAsync(address + "/api/receiver");
        if (json.Length > 4096) throw new InvalidOperationException("接收端握手过大");
        var receiver = JsonUtility.FromJson<Envelope>(json)?.data;
        if (receiver == null || !receiver.Valid || (expected != null && receiver.id != expected))
            throw new InvalidOperationException("接收端身份不匹配，已保留原目标的待上传资料");
        receiver.address = address;
        return receiver;
    }

}

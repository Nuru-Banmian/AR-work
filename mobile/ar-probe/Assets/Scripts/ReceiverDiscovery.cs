using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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

public static class ReceiverDiscovery
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

    public static Task<ReceiverInfo[]> Scan() => Task.Run(() => {
        var found = new Dictionary<string, ReceiverInfo>();
        using (var socket = new UdpClient(AddressFamily.InterNetwork)) {
            socket.EnableBroadcast = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.Client.ReceiveTimeout = 300;
            var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces()) {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var item in network.GetIPProperties().UnicastAddresses) {
                    if (item.Address.AddressFamily != AddressFamily.InterNetwork || item.IPv4Mask == null) continue;
                    var ip = item.Address.GetAddressBytes(); var mask = item.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    targets.Add(new IPAddress(ip));
                }
            }
            var query = Encoding.ASCII.GetBytes("ARWORK-DISCOVER-1");
            foreach (var target in targets) { try { socket.Send(query, query.Length, new IPEndPoint(target, 18087)); } catch (SocketException) { } }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline) {
                IPEndPoint source = null;
                try {
                    var bytes = socket.Receive(ref source);
                    if (bytes.Length > 4096) continue;
                    var receiver = JsonUtility.FromJson<ReceiverInfo>(Encoding.UTF8.GetString(bytes));
                    if (receiver == null || !receiver.Valid) continue;
                    receiver.address = "http://" + source.Address + ":" + receiver.port;
                    // Keep separate addresses until HTTP verification; an invalid advertisement must not hide a valid one.
                    if (found.Count < 16) found[receiver.id + receiver.address] = receiver;
                } catch (SocketException) { } catch (ArgumentException) { }
            }
        }
        return new List<ReceiverInfo>(found.Values).ToArray();
    });
}

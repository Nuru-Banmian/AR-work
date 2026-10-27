using System;
using System.Text;
using UnityEngine;

[Serializable] public sealed class RemarkPayload
{
    public int reportVersion = 2;
    public IssueScanModel model;
    public string snapshot;
    public IssueHistoryPanel.Position position;

    public static string Freeze(IssueScanModel model, string snapshot, Vector3 point)
    {
        if (model == null || string.IsNullOrEmpty(snapshot)) throw new ArgumentException("本次扫描模型和已确认快照必需。");
        var body = JsonUtility.ToJson(new RemarkPayload { model = model, snapshot = snapshot,
            position = new IssueHistoryPanel.Position { x = point.x, y = point.y, z = point.z } });
        if (Encoding.UTF8.GetByteCount(body) > 32 * 1024 * 1024) throw new ArgumentException("完整替换资料超过 32 MiB，请减少扫描范围。");
        return body;
    }
}

using System;
using UnityEngine;
using UnityEngine.Networking;

internal static class InspectionUi
{
    private static Font font;
    internal static Font Font => font != null ? font : (font = Resources.Load<Font>("InspectionChinese"));
    internal static string Priority(string value) => value == "high" ? "高" : value == "low" ? "低" : "中";
    internal static string Status(string value) => value == "resolved" ? "已解决" : value == "in_progress" ? "处理中" : "待处理";
    internal static string Time(string value) => DateTimeOffset.TryParse(value, out var time) ? time.ToLocalTime().ToString("MM-dd HH:mm") : "未知";
    internal static string Error(UnityWebRequest request)
    {
        switch (request.responseCode)
        {
            case 400: return "填写内容有误，请检查后重试。";
            case 404: return "未找到该问题，请刷新列表。";
            case 409: return "提交记录冲突，请保留应用数据并联系维护人员。";
            case 500: return "服务器保存失败，请稍后重试。";
            default: return "连接失败，请检查服务地址和网络。";
        }
    }
}

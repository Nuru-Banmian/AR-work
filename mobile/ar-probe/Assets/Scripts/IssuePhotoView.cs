using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// One owned texture/request per detail page. Navigation cancels the old download.
public sealed class IssuePhotoView : MonoBehaviour
{
    private GameObject viewer;
    private RawImage thumbnail, fullImage;
    private Text feedback;
    private Button open, retry;
    private Texture2D texture;
    private UnityWebRequest request;
    private Coroutine loading;
    private string source;

    public void Initialize(Transform detail, Transform overlayParent)
    {
        var area = new GameObject("现场照片", typeof(RectTransform), typeof(Image));
        area.transform.SetParent(detail, false);
        var rect = area.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.03f, 0); rect.anchorMax = new Vector2(0.97f, 0);
        rect.pivot = new Vector2(0.5f, 0); rect.anchoredPosition = new Vector2(0, 505); rect.sizeDelta = new Vector2(0, 220);
        area.GetComponent<Image>().color = new Color(0.08f, 0.12f, 0.15f);
        thumbnail = Picture(area.transform);
        thumbnail.transform.parent.GetComponent<RectTransform>().anchorMax = new Vector2(0.55f, 1);
        feedback = IssueReportForm.Label(area.transform, 25);
        feedback.rectTransform.anchorMin = new Vector2(0.57f, 0.68f);
        feedback.rectTransform.offsetMin = feedback.rectTransform.offsetMax = Vector2.zero;
        open = IssueReportForm.ButtonAt(area.transform, "查看大图", 80, 0.57f, 0.98f, () => {
            if (texture == null) return;
            viewer.transform.SetAsLastSibling(); viewer.SetActive(true);
        });
        retry = IssueReportForm.ButtonAt(area.transform, "重试图片", 10, 0.57f, 0.98f, () => BeginDownload());
        viewer = new GameObject("现场照片大图", typeof(RectTransform), typeof(Image));
        viewer.transform.SetParent(overlayParent, false);
        var fullRect = viewer.GetComponent<RectTransform>();
        fullRect.anchorMin = Vector2.zero; fullRect.anchorMax = Vector2.one;
        fullRect.offsetMin = fullRect.offsetMax = Vector2.zero;
        viewer.GetComponent<Image>().color = new Color(0.02f, 0.04f, 0.06f, 1);
        fullImage = Picture(viewer.transform);
        fullImage.transform.parent.GetComponent<RectTransform>().offsetMin = new Vector2(15, 100); fullImage.transform.parent.GetComponent<RectTransform>().offsetMax = new Vector2(-15, -20);
        IssueReportForm.ButtonAt(viewer.transform, "关闭大图", 20, 0.1f, 0.9f, () => viewer.SetActive(false));
        viewer.SetActive(false);
    }

    private static RawImage Picture(Transform parent)
    {
        var frame = new GameObject("照片区域", typeof(RectTransform)).GetComponent<RectTransform>();
        frame.SetParent(parent, false); frame.anchorMin = Vector2.zero; frame.anchorMax = Vector2.one;
        frame.offsetMin = frame.offsetMax = Vector2.zero;
        var image = new GameObject("照片（保持比例）", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        image.transform.SetParent(frame, false);
        image.rectTransform.anchorMin = Vector2.zero; image.rectTransform.anchorMax = Vector2.one;
        image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
        image.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        image.enabled = false; return image;
    }

    public void Show(string server, string id, string snapshotUrl, string version)
    {
        var next = string.IsNullOrEmpty(snapshotUrl) ? null : server + "/api/issues/" + Uri.EscapeDataString(id) + "/snapshot?v=" + Uri.EscapeDataString(version ?? "");
        if (next != null && source == next) return;
        Clear(); source = next;
        feedback.text = next == null ? "无快照（旧记录）" : "正在加载照片…";
        retry.interactable = next != null;
        if (next != null) BeginDownload();
    }

    private void BeginDownload()
    {
        if (source == null || loading != null) return;
        loading = StartCoroutine(Download());
    }

    private IEnumerator Download()
    {
        feedback.text = "正在加载照片…"; retry.interactable = false;
        using (var download = UnityWebRequestTexture.GetTexture(source, true))
        {
            request = download; download.timeout = 15;
            yield return download.SendWebRequest();
            if (download.result != UnityWebRequest.Result.Success) feedback.text = "图片加载失败，可重试";
            else
            {
                if (texture != null) Destroy(texture);
                texture = DownloadHandlerTexture.GetContent(download);
                if (texture == null) feedback.text = "图片无效，可重试";
                else
                {
                    thumbnail.texture = fullImage.texture = texture;
                    foreach (var image in new[] { thumbnail, fullImage }) {
                        image.enabled = true;
                        image.GetComponent<AspectRatioFitter>().aspectRatio = (float)texture.width / texture.height;
                    }
                    open.interactable = true; feedback.text = "现场照片";
                }
            }
            request = null;
        }
        retry.interactable = true; loading = null;
    }

    public void Clear()
    {
        request?.Abort();
        if (loading != null) StopCoroutine(loading);
        request?.Dispose(); request = null; loading = null; source = null;
        if (texture != null) Destroy(texture); texture = null;
        if (thumbnail != null) { thumbnail.texture = null; thumbnail.enabled = false; }
        if (fullImage != null) { fullImage.texture = null; fullImage.enabled = false; }
        if (viewer != null) viewer.SetActive(false);
        if (open != null) open.interactable = false;
    }
    private void OnDestroy() { Clear(); if (viewer != null) Destroy(viewer); }
}

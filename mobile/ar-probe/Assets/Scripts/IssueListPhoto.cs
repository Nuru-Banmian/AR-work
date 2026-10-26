using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// Each row owns its request and small display texture; leaving the list releases both.
public sealed class IssueListPhoto : MonoBehaviour
{
    private RawImage image;
    private Text feedback;
    private Texture2D texture;
    private UnityWebRequest request;

    public void Initialize(Button row, string address, string encodedPhoto = null)
    {
        var text = row.GetComponentInChildren<Text>();
        text.rectTransform.anchorMin = new Vector2(0.19f, 0);
        var frame = new GameObject("列表快照", typeof(RectTransform)).GetComponent<RectTransform>();
        frame.SetParent(row.transform, false);
        frame.anchorMin = new Vector2(0.01f, 0.05f); frame.anchorMax = new Vector2(0.18f, 0.95f);
        frame.offsetMin = frame.offsetMax = Vector2.zero;
        image = new GameObject("缩略图", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        image.transform.SetParent(frame, false);
        image.rectTransform.anchorMin = Vector2.zero; image.rectTransform.anchorMax = Vector2.one;
        image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
        image.raycastTarget = false; image.enabled = false;
        image.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        feedback = IssueReportForm.Label(frame, 22); feedback.raycastTarget = false;
        feedback.text = "无快照";
        if (!string.IsNullOrEmpty(encodedPhoto)) {
            var decoded = new Texture2D(2, 2);
            try {
                if (!decoded.LoadImage(Convert.FromBase64String(encodedPhoto))) throw new ArgumentException("Invalid photo");
                Display(decoded);
            } catch (ArgumentException) { feedback.text = "图片失败"; }
            finally { Destroy(decoded); }
        } else if (address != null) StartCoroutine(Download(address));
    }

    private IEnumerator Download(string address)
    {
        feedback.text = "加载中";
        using (var download = UnityWebRequestTexture.GetTexture(address, true)) {
            request = download; download.timeout = 15;
            yield return download.SendWebRequest();
            if (download.result != UnityWebRequest.Result.Success) feedback.text = "图片失败\n刷新重试";
            else {
                var decoded = DownloadHandlerTexture.GetContent(download);
                if (decoded == null) feedback.text = "图片失败";
                else { Display(decoded); Destroy(decoded); }
            }
            request = null;
        }
    }

    private void Display(Texture2D decoded)
    {
        float scale = Mathf.Min(1f, 128f / Mathf.Max(decoded.width, decoded.height));
        var target = RenderTexture.GetTemporary(Mathf.Max(1, Mathf.RoundToInt(decoded.width * scale)),
            Mathf.Max(1, Mathf.RoundToInt(decoded.height * scale)));
        var previous = RenderTexture.active;
        try {
            Graphics.Blit(decoded, target); RenderTexture.active = target;
            texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply(false, true);
            image.texture = texture; image.enabled = true;
            image.GetComponent<AspectRatioFitter>().aspectRatio = (float)decoded.width / decoded.height;
            feedback.text = "";
        } finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
    }

    private void OnDisable()
    {
        StopAllCoroutines(); request?.Abort(); request?.Dispose(); request = null;
        if (image != null) image.texture = null;
        if (texture != null) Destroy(texture); texture = null;
    }
}

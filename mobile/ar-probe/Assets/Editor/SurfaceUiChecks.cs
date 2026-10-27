using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

[InitializeOnLoad]
public static class SurfaceUiChecks
{
    private const string Flag = "arwork.surface-ui-check";
    static SurfaceUiChecks()
    {
        EditorApplication.playModeStateChanged += state => {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Flag, false))
                new GameObject("Surface UI checks").AddComponent<SurfaceUiCheckRunner>();
        };
    }
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Flag, true); EditorApplication.EnterPlaymode();
    }
    internal static void Finish(int code)
    { SessionState.SetBool(Flag, false); EditorApplication.Exit(code); }
}

public sealed class SurfaceUiCheckRunner : MonoBehaviour
{
    private static FieldInfo Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static void Set(object obj, string name, object value) => Field(obj, name).SetValue(obj, value);
    private static T Get<T>(object obj, string name) => (T)Field(obj, name).GetValue(obj);
    private static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    private IEnumerator Start()
    {
        var flow = Verify();
        while (true)
        {
            bool next;
            try { next = flow.MoveNext(); }
            catch (Exception error) { Debug.LogException(error); SurfaceUiChecks.Finish(1); yield break; }
            if (!next) break;
            yield return flow.Current;
        }
        Debug.Log("SURFACE_UI_CHECKS_PASSED"); SurfaceUiChecks.Finish(0);
    }
    private IEnumerator Verify()
    {
        // No app startup, HTTP, PlayerPrefs, XR loader or device data is touched.
        var ownerRoot = new GameObject("Inactive probe owner"); ownerRoot.SetActive(false);
        var owner = ownerRoot.AddComponent<ArProbe>();
        owner.planeMaterial = new Material(Shader.Find("Sprites/Default"));
        var cameraRoot = new GameObject("Inactive camera"); cameraRoot.SetActive(false);
        var camera = cameraRoot.AddComponent<Camera>();
        cameraRoot.AddComponent<ARCameraManager>();
        var occlusion = cameraRoot.AddComponent<AROcclusionManager>();
        var ui = new GameObject("Isolated UI", typeof(Canvas), typeof(CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
        ui.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scanner = new GameObject("Scanner").AddComponent<LocalSurfaceScanner>();
        scanner.Initialize(owner, ui.transform, camera, occlusion);
        scanner.SetVisible(true);
        Canvas.ForceUpdateCanvases();
        var frame = Get<RectTransform>(scanner, "scanFrame");
        var corners = new Vector3[4]; frame.GetWorldCorners(corners);
        Require(scanner.IsInsideScanFrame((corners[0]+corners[2])*0.5f)
            && !scanner.IsInsideScanFrame(new Vector2(corners[0].x-20, corners[0].y))
            && !scanner.IsInsideScanFrame(new Vector2(corners[2].x+20, corners[2].y)),
            "visible scan frame includes its centre and excludes samples outside either edge");
        Require(Mathf.Abs(Vector3.Distance(corners[0],corners[1])-Vector3.Distance(corners[1],corners[2]))<1,
            "scan frame is square in screen pixels");
        foreach (var image in frame.GetComponentsInChildren<Image>())
            Require(!image.raycastTarget, "scan frame does not block target picking");
        var controls = Get<GameObject>(scanner, "controls"); var rect = controls.GetComponent<RectTransform>();
        Require(rect.anchorMin.y == 0 && rect.pivot.y == 0 && rect.anchoredPosition.y == 170
            && rect.rect.height == 320, "scan controls stay at bottom above capture buttons");
        var data = new SurfaceMeshData(0.5f);
        data.Vertices.AddRange(new[] {new Vector3(0,0,0.2f),new Vector3(0.02f,0,0.2f),new Vector3(0,0.02f,0.2f)});
        data.Triangles.AddRange(new[] {0,1,2});
        var mesh = new Mesh(); data.WriteTo(mesh);
        var anchorRoot = new GameObject("Old scan anchor"); anchorRoot.SetActive(false);
        var anchor = anchorRoot.AddComponent<ARAnchor>();
        var surface = new GameObject("Old surface", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
        surface.transform.SetParent(anchorRoot.transform, false);
        var collider = surface.GetComponent<MeshCollider>(); collider.sharedMesh = mesh;
        Set(scanner, "data", new TsdfVolume(0.5f,16)); Set(scanner, "geometry", data); Set(scanner, "mesh", mesh); Set(scanner, "anchor", anchor);
        Set(scanner, "surface", surface); Set(scanner, "meshCollider", collider); Set(scanner, "scanning", true);
        Get<Button>(scanner,"scanButton").onClick.Invoke();
        Require(!surface.GetComponent<Renderer>().enabled && collider.enabled && collider.sharedMesh==mesh,
            "pause hides the surface immediately but keeps its placement collider");
        Set(scanner,"scanning",true);
        var clear = controls.transform.Find("清除扫描").GetComponent<Button>();
        var pending = new TaskCompletionSource<SurfaceMeshData>();
        Set(scanner, "fusionTask", pending.Task); Set(scanner, "taskVolume", Get<TsdfVolume>(scanner, "data"));
        clear.onClick.Invoke();
        Require(!scanner.RequiresDepth && !scanner.BlocksPlacement,
            "clear stops scanning");
        Require(Get<TsdfVolume>(scanner, "data") == null && Get<Mesh>(scanner, "mesh") == null
            && !surface.activeSelf && !collider.enabled && !scanner.Raycast(Vector2.zero, out _),
            "clear immediately removes visible and selectable old geometry: data=" + (Get<TsdfVolume>(scanner, "data") == null)
            + " mesh=" + (Get<Mesh>(scanner, "mesh") == null) + " hidden=" + !surface.activeSelf
            + " colliderDisabled=" + !collider.enabled);
        Require(Get<Button>(scanner, "sizeButton").interactable && !Get<Button>(scanner, "previewButton").interactable,
            "clear restores new-target controls and disables stale preview");
        pending.SetResult(data);
        yield return null;
        Require(Get<SurfaceMeshData>(scanner, "geometry") == null, "completed stale fusion cannot restore a cleared scan");
        Require(anchor == null && mesh == null && surface == null, "clear releases old scan objects next frame");
        clear.onClick.Invoke();
        Get<Button>(scanner, "scanButton").onClick.Invoke();
        Require(scanner.RequiresDepth, "new scan can start after repeated clear");
        clear.onClick.Invoke();
        Set(scanner, "previewAfterFusion", true);
        scanner.SetVisible(false);
        Require(!Get<bool>(scanner, "previewAfterFusion"), "leaving camera cancels deferred preview");
        Require(!controls.activeSelf && !scanner.BlocksPlacement, "leaving capture does not leave invisible interaction locks");
        Destroy(scanner.gameObject); Destroy(owner.planeMaterial); Destroy(ownerRoot); Destroy(cameraRoot); Destroy(ui);
    }
}

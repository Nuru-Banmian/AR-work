using System.Collections.Generic;
using UnityEngine;

// Transfer object for a fused surface, plus camera coordinate helpers.
public sealed class SurfaceMeshData
{
    public const int MaxVertices = 90000, MaxTriangles = 160000;
    public readonly List<Vector3> Vertices = new List<Vector3>();
    public readonly List<int> Triangles = new List<int>();
    public readonly List<Color32> Colors = new List<Color32>();
    public bool Truncated;
    public bool Full => Vertices.Count >= MaxVertices - 2 || Triangles.Count / 3 >= MaxTriangles;
    public float HalfExtent { get; }
    public SurfaceMeshData(float halfExtent) { HalfExtent = halfExtent; }

    public void RemoveSmallIslands()
    {
        if (Triangles.Count < 6) return;
        var parent=new int[Vertices.Count]; var counts=new int[Vertices.Count];
        for (int i=0;i<parent.Length;i++) parent[i]=i;
        for (int i=0;i<Triangles.Count;i+=3)
        {
            int a=Root(parent,Triangles[i]), b=Root(parent,Triangles[i+1]), c=Root(parent,Triangles[i+2]);
            parent[b]=a; parent[c]=a;
        }
        int biggest=0;
        for (int i=0;i<Triangles.Count;i+=3) { int root=Root(parent,Triangles[i]); counts[root]++; if (counts[root]>counts[biggest]) biggest=root; }
        int minimum=Mathf.Max(48,counts[biggest]/50);
        var retained=new List<int>(); var remap=new int[Vertices.Count];
        for (int i=0;i<remap.Length;i++) remap[i]=-1;
        var points=new List<Vector3>(); var colors=new List<Color32>();
        for (int i=0;i<Triangles.Count;i+=3)
        {
            int root=Root(parent,Triangles[i]); if (root!=biggest && counts[root]<minimum) continue;
            for (int j=0;j<3;j++)
            {
                int original=Triangles[i+j];
                if (remap[original]<0) { remap[original]=points.Count; points.Add(Vertices[original]); if (Colors.Count==Vertices.Count) colors.Add(Colors[original]); }
                retained.Add(remap[original]);
            }
        }
        Vertices.Clear(); Vertices.AddRange(points); Colors.Clear(); Colors.AddRange(colors); Triangles.Clear(); Triangles.AddRange(retained);
    }
    private static int Root(int[] parent,int index)
    {
        while (parent[index]!=index) { parent[index]=parent[parent[index]]; index=parent[index]; } return index;
    }

    public static bool TryMedianDepth(float[] samples, out float depth, out string reason)
    {
        depth=0; var valid=new List<float>();
        foreach (float sample in samples) if (!float.IsNaN(sample) && !float.IsInfinity(sample) && sample>0) valid.Add(sample);
        if (valid.Count<5) { reason="目标处暂无有效深度，请缓慢横移几秒后重试。"; return false; }
        valid.Sort(); depth=valid[valid.Count/2];
        if (depth<0.5f) { reason="目标深度约 "+depth.ToString("F2")+" 米，请后退至至少 0.5 米。"; return false; }
        if (depth>3f) { reason="目标深度约 "+depth.ToString("F2")+" 米，超出 3 米范围；请等待深度稳定。"; return false; }
        if (valid[valid.Count-2]-valid[1]>Mathf.Max(0.06f,depth*0.06f))
        { reason="目标边缘深度不连续，请点击物品内部较平整的位置。"; return false; }
        reason=null; return true;
    }
    public static Vector3 Unproject(float x, float y, float depth, int width, int height,
        Vector2 focal, Vector2 principal, Vector2Int resolution)
    {
        // Native camera/depth images share landscape coordinates; screen portrait rotation is not applied here.
        float scale = Mathf.Max((float)width / resolution.x, (float)height / resolution.y);
        float u = (x + 0.5f - width * 0.5f) / scale + resolution.x * 0.5f;
        float v = (y + 0.5f - height * 0.5f) / scale + resolution.y * 0.5f;
        return new Vector3((u-principal.x) * depth / focal.x, -(v-principal.y) * depth / focal.y, depth);
    }
    public static Matrix4x4 NativeCameraToWorld(Matrix4x4 cameraToWorld, ScreenOrientation orientation)
    {
        float angle = orientation == ScreenOrientation.Portrait ? -90
            : orientation == ScreenOrientation.PortraitUpsideDown ? 90
            : orientation == ScreenOrientation.LandscapeRight ? 180 : 0;
        return cameraToWorld * Matrix4x4.Rotate(Quaternion.Euler(0, 0, angle));
    }
    public void WriteTo(Mesh mesh)
    {
        mesh.indexFormat = Vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.Clear(); mesh.SetVertices(Vertices); mesh.SetTriangles(Triangles, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        if (Colors.Count == Vertices.Count) { mesh.SetColors(Colors); return; }
        var normals = mesh.normals;
        var colors = new Color[normals.Length];
        var light = new Vector3(-0.4f, 0.7f, -0.6f).normalized;
        for (int i = 0; i < colors.Length; i++)
        {
            float shade = 0.35f + 0.65f * Mathf.Abs(Vector3.Dot(normals[i], light));
            colors[i] = new Color(shade, shade, shade, 1);
        }
        mesh.colors = colors;
    }
}

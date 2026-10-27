using System;
using System.Collections.Generic;
using UnityEngine;

// Projective, bounded TSDF integration. ARCore supplies tracking; this is not an ICP/SLAM implementation.
// Runs on a worker with immutable frame buffers. No Unity objects are accessed by this module.
public sealed class TsdfVolume
{
    public sealed class Frame
    {
        public int Width, Height, ColorWidth, ColorHeight;
        public float[] Depth;
        public byte[] Confidence, Rgb;
        public bool[] Mask;
        public Vector2 Focal, Principal;
        public Matrix4x4 VolumeToCamera;
    }
    public readonly int Resolution;
    public readonly float Side, Spacing, Truncation;
    private readonly float[] distance, weight;
    private readonly byte[] observations;
    private readonly uint[] color;
    public int Frames { get; private set; }
    public int LastSupportedPixels { get; private set; }
    public int ObservedVoxels { get; private set; }
    public float LastIntegrationMilliseconds { get; private set; }
    public bool HasColor { get; private set; }
    public float LastAlignmentMetres { get; private set; }
    public int LastAlignmentSupport { get; private set; }
    public TsdfVolume(float side, int resolution = 96)
    {
        if (side < 0.1f || side > 2f || resolution < 16 || resolution > 128) throw new ArgumentOutOfRangeException();
        Side = side; Resolution = resolution; Spacing = side/(resolution-1); Truncation = Spacing*4;
        int count = resolution*resolution*resolution;
        distance = new float[count]; weight = new float[count]; observations = new byte[count]; color = new uint[count];
    }
    public void Integrate(Frame frame)
    {
        if (frame == null || frame.Width <= 1 || frame.Height <= 1 || frame.Depth == null
            || frame.Depth.Length != frame.Width*frame.Height || frame.Mask == null || frame.Mask.Length != frame.Depth.Length
            || frame.Focal.x <= 0 || frame.Focal.y <= 0) throw new ArgumentException("Invalid calibrated depth frame");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int supported = 0;
        for (int i = 0; i < frame.Depth.Length; i++)
            if (frame.Mask[i] && ValidDepth(frame.Depth[i])) supported++;
        LastSupportedPixels = supported;
        if (supported < 16) return;
        bool colored = frame.Rgb != null && frame.ColorWidth > 0 && frame.ColorHeight > 0
            && frame.Rgb.Length == frame.ColorWidth*frame.ColorHeight*3;
        var registered = AlignTranslation(frame);
        var origin = registered.MultiplyPoint3x4(Vector3.one*(-Side*0.5f));
        var dx = registered.MultiplyVector(new Vector3(Spacing, 0, 0));
        var dy = registered.MultiplyVector(new Vector3(0, Spacing, 0));
        var dz = registered.MultiplyVector(new Vector3(0, 0, Spacing));
        int n = Resolution;
        for (int z = 0; z < n; z++) for (int y = 0; y < n; y++)
        {
            Vector3 p = origin + dz*z + dy*y;
            int index = n*(y+n*z);
            for (int x = 0; x < n; x++, index++, p += dx)
            {
                if (p.z < 0.1f) continue;
                int u = Mathf.RoundToInt(frame.Focal.x*p.x/p.z+frame.Principal.x);
                int v = Mathf.RoundToInt(-frame.Focal.y*p.y/p.z+frame.Principal.y);
                if (u < 1 || v < 1 || u >= frame.Width-1 || v >= frame.Height-1) continue;
                int pixel = v*frame.Width+u;
                float depth = frame.Depth[pixel];
                if (!frame.Mask[pixel] || !ValidDepth(depth)) continue;
                // Discontinuous pixels are not reliable evidence for a thin object silhouette.
                float left = frame.Depth[pixel-1], right = frame.Depth[pixel+1];
                float above = frame.Depth[pixel-frame.Width], below = frame.Depth[pixel+frame.Width];
                float edge = Mathf.Max(0.025f, depth*0.02f);
                if (!ValidDepth(left) || !ValidDepth(right) || !ValidDepth(above) || !ValidDepth(below)
                    || Mathf.Abs(depth-left)>edge || Mathf.Abs(depth-right)>edge
                    || Mathf.Abs(depth-above)>edge || Mathf.Abs(depth-below)>edge) continue;
                float sdf = (depth-p.z)*Mathf.Sqrt(1+(p.x*p.x+p.y*p.y)/(p.z*p.z));
                if (sdf < -Truncation) continue;
                float value = Mathf.Min(1, sdf/Truncation);
                byte confidence = frame.Confidence != null && frame.Confidence.Length == frame.Depth.Length ? frame.Confidence[pixel] : (byte)0;
                // Dense depth without raw support is interpolated, so it needs multiple observations.
                float observationWeight = confidence >= 128 ? confidence/255f : 0.25f;
                float old = weight[index];
                // Reject a large residual once several frames already agree near the zero level set.
                if (old >= 2 && Mathf.Abs(distance[index]) < 0.8f && Mathf.Abs(value-distance[index]) > 1.25f) continue;
                float sum = old+observationWeight;
                distance[index] = (distance[index]*old+value*observationWeight)/sum;
                weight[index] = Mathf.Min(32, sum);
                if (observations[index] == 0) ObservedVoxels++;
                if (observations[index] < 255) observations[index]++;
                if (colored && Mathf.Abs(sdf) <= Truncation)
                {
                    float colorScale=Mathf.Max((float)frame.Width/frame.ColorWidth,(float)frame.Height/frame.ColorHeight);
                    int cu = Mathf.Clamp(Mathf.FloorToInt((u+0.5f-frame.Width*0.5f)/colorScale+frame.ColorWidth*0.5f),0,frame.ColorWidth-1);
                    int cv = Mathf.Clamp(Mathf.FloorToInt((v+0.5f-frame.Height*0.5f)/colorScale+frame.ColorHeight*0.5f),0,frame.ColorHeight-1);
                    int ci = (cv*frame.ColorWidth+cu)*3;
                    uint previous = color[index];
                    float previousWeight = (previous >> 24) == 0 ? 0 : Mathf.Min(old, 8);
                    float combined = previousWeight+observationWeight;
                    uint r = (uint)(((previous & 255)*previousWeight+frame.Rgb[ci]*observationWeight)/combined);
                    uint g = (uint)((((previous >> 8) & 255)*previousWeight+frame.Rgb[ci+1]*observationWeight)/combined);
                    uint b = (uint)((((previous >> 16) & 255)*previousWeight+frame.Rgb[ci+2]*observationWeight)/combined);
                    color[index] = r | g << 8 | b << 16 | 255u << 24;
                }
            }
        }
        Frames++; HasColor |= colored;
        LastIntegrationMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
    }
    // A bounded point-to-plane correction against the already observed TSDF. ARCore remains
    // the pose source; this only compensates small frame residuals, never global relocalisation.
    private Matrix4x4 AlignTranslation(Frame frame)
    {
        LastAlignmentMetres=0; LastAlignmentSupport=0;
        if (Frames<4) return frame.VolumeToCamera;
        var cameraToVolume=frame.VolumeToCamera.inverse;
        Vector3 correction=Vector3.zero;
        for (int iteration=0;iteration<4;iteration++)
        {
            var system=new double[3,4]; int count=0;
            for (int v=1;v<frame.Height-1;v+=2) for (int u=1;u<frame.Width-1;u+=2)
            {
                int pixel=v*frame.Width+u; float depth=frame.Depth[pixel];
                if (!frame.Mask[pixel] || !ValidDepth(depth) || frame.Confidence==null
                    || frame.Confidence.Length!=frame.Depth.Length || frame.Confidence[pixel]<128) continue;
                var point=cameraToVolume.MultiplyPoint3x4(new Vector3((u-frame.Principal.x)*depth/frame.Focal.x,
                    -(v-frame.Principal.y)*depth/frame.Focal.y,depth))+correction;
                if (!SampleGradient(point,out float residual,out Vector3 gradient) || Mathf.Abs(residual)>Truncation*0.9f) continue;
                float length=gradient.magnitude;
                if (length<0.3f || length>2.5f) continue;
                var normal=gradient/length; float distance=residual/length;
                float robust=Mathf.Min(1,0.008f/Mathf.Max(Mathf.Abs(distance),0.0001f));
                for (int row=0;row<3;row++)
                {
                    for (int col=0;col<3;col++) system[row,col]+=robust*normal[row]*normal[col];
                    system[row,3]-=robust*normal[row]*distance;
                }
                count++;
            }
            LastAlignmentSupport=count;
            if (count<32) break;
            // Prior keeps under-constrained tangential directions close to the tracking pose.
            for (int axis=0;axis<3;axis++) system[axis,axis]+=count*0.01;
            for (int pivot=0;pivot<3;pivot++)
            {
                double divisor=system[pivot,pivot];
                for (int col=pivot;col<4;col++) system[pivot,col]/=divisor;
                for (int row=0;row<3;row++) if (row!=pivot)
                {
                    double factor=system[row,pivot];
                    for (int col=pivot;col<4;col++) system[row,col]-=factor*system[pivot,col];
                }
            }
            var step=new Vector3((float)system[0,3],(float)system[1,3],(float)system[2,3]);
            if (float.IsNaN(step.sqrMagnitude) || float.IsInfinity(step.sqrMagnitude)) break;
            correction=Vector3.ClampMagnitude(correction+Vector3.ClampMagnitude(step,0.01f),0.03f);
            if (step.magnitude<0.0003f) break;
        }
        LastAlignmentMetres=correction.magnitude;
        return frame.VolumeToCamera*Matrix4x4.Translate(-correction);
    }
    private bool SampleGradient(Vector3 point,out float residual,out Vector3 gradient)
    {
        residual=0; gradient=Vector3.zero;
        int x=Mathf.RoundToInt((point.x+Side/2)/Spacing),y=Mathf.RoundToInt((point.y+Side/2)/Spacing),z=Mathf.RoundToInt((point.z+Side/2)/Spacing);
        int n=Resolution;
        if (x<1 || y<1 || z<1 || x>=n-1 || y>=n-1 || z>=n-1) return false;
        int i=x+n*(y+n*z);
        if (!Known(i)||!Known(i-1)||!Known(i+1)||!Known(i-n)||!Known(i+n)||!Known(i-n*n)||!Known(i+n*n)) return false;
        gradient=new Vector3(distance[i+1]-distance[i-1],distance[i+n]-distance[i-n],distance[i+n*n]-distance[i-n*n])*(Truncation/(2*Spacing));
        residual=distance[i]*Truncation+Vector3.Dot(gradient,point-Position(i));
        return true;
    }
    private bool Known(int i) => observations[i]>=3 && weight[i]>=0.6f;
    private static bool ValidDepth(float d) => !float.IsNaN(d) && !float.IsInfinity(d) && d >= 0.5f && d <= 3f;
    private Vector3 Position(int index)
    {
        int n = Resolution, x = index % n, y = index/n % n, z = index/(n*n);
        return new Vector3(x*Spacing-Side/2, y*Spacing-Side/2, z*Spacing-Side/2);
    }
    private static readonly int[,] Tetrahedra = { {0,5,1,6}, {0,1,2,6}, {0,2,3,6}, {0,3,7,6}, {0,7,4,6}, {0,4,5,6} };
    public SurfaceMeshData Extract()
    {
        var result = new SurfaceMeshData(Side/2);
        if (Frames < 3) return result;
        var edges = new Dictionary<long, int>();
        var cube = new int[8]; var inside = new int[4]; var outside = new int[4];
        int n = Resolution;
        for (int z = 0; z < n-1; z++) for (int y = 0; y < n-1; y++) for (int x = 0; x < n-1; x++)
        {
            int a = x+n*(y+n*z);
            cube[0]=a; cube[1]=a+1; cube[2]=a+1+n; cube[3]=a+n;
            cube[4]=a+n*n; cube[5]=a+1+n*n; cube[6]=a+1+n+n*n; cube[7]=a+n+n*n;
            bool valid = true, negative = false, positive = false;
            for (int i = 0; i < 8; i++)
            {
                int id = cube[i];
                if (observations[id] < 3 || weight[id] < 0.6f) { valid=false; break; }
                negative |= distance[id]<0; positive |= distance[id]>=0;
            }
            // Unknown voxels never become a fabricated closing surface.
            if (!valid || !negative || !positive) continue;
            for (int t = 0; t < 6; t++)
            {
                int ni=0, no=0; Vector3 direction=Vector3.zero;
                for (int i = 0; i < 4; i++)
                {
                    int id=cube[Tetrahedra[t,i]];
                    if (distance[id]<0) inside[ni++]=id; else outside[no++]=id;
                }
                if (ni==0 || no==0) continue;
                for (int i=0; i<no; i++) direction += Position(outside[i])/no;
                for (int i=0; i<ni; i++) direction -= Position(inside[i])/ni;
                if (ni==1)
                    Face(result, Edge(result,edges,inside[0],outside[0]), Edge(result,edges,inside[0],outside[1]), Edge(result,edges,inside[0],outside[2]), direction);
                else if (no==1)
                    Face(result, Edge(result,edges,outside[0],inside[0]), Edge(result,edges,outside[0],inside[1]), Edge(result,edges,outside[0],inside[2]), direction);
                else
                {
                    int p=Edge(result,edges,inside[0],outside[0]), q=Edge(result,edges,inside[0],outside[1]);
                    int r=Edge(result,edges,inside[1],outside[0]), s=Edge(result,edges,inside[1],outside[1]);
                    Face(result,p,q,r,direction); Face(result,q,s,r,direction);
                }
                if (result.Full) { result.Truncated = true; result.RemoveSmallIslands(); return result; }
            }
        }
        result.RemoveSmallIslands(); return result;
    }
    private int Edge(SurfaceMeshData result, Dictionary<long,int> edges, int a, int b)
    {
        long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        if (edges.TryGetValue(key,out int index)) return index;
        float fraction = distance[a]/(distance[a]-distance[b]);
        index=result.Vertices.Count; result.Vertices.Add(Vector3.LerpUnclamped(Position(a),Position(b),fraction));
        uint ca=color[a], cb=color[b];
        if ((ca>>24)==0) ca=cb; if ((cb>>24)==0) cb=ca;
        var c1=new Color32((byte)ca,(byte)(ca>>8),(byte)(ca>>16),255);
        var c2=new Color32((byte)cb,(byte)(cb>>8),(byte)(cb>>16),255);
        result.Colors.Add(HasColor ? Color32.Lerp(c1,c2,fraction) : new Color32(80,190,190,255));
        edges.Add(key,index); return index;
    }
    private static void Face(SurfaceMeshData mesh, int a, int b, int c, Vector3 outward)
    {
        var normal=Vector3.Cross(mesh.Vertices[b]-mesh.Vertices[a],mesh.Vertices[c]-mesh.Vertices[a]);
        if (normal.sqrMagnitude < 1e-14f) return;
        if (Vector3.Dot(normal,outward)<0) { int swap=b; b=c; c=swap; }
        mesh.Triangles.Add(a); mesh.Triangles.Add(b); mesh.Triangles.Add(c);
    }
}

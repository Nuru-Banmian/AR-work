using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class SurfaceScanChecks
{
    public static void Run()
    {
        string temporary=Path.Combine(Path.GetTempPath(),"arwork-surface-"+Guid.NewGuid().ToString("N"));
        try
        {
            Coordinates();
            var pointed=(Mesh)typeof(ArProbe).GetMethod("CreateMarkerMesh",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,null);
            Require(pointed.vertices.Length==4 && pointed.vertices[0]==Vector3.zero && pointed.triangles.Length==12,
                "triangular marker apex is exactly the selected position");
            Require(Mathf.Abs(pointed.bounds.size.x-0.02f)<1e-5 && pointed.bounds.min.y==0 && pointed.bounds.max.y<=0.03f,
                "pointed marker is smaller than the former 5cm cube");
            UnityEngine.Object.DestroyImmediate(pointed);
            Require(!SurfaceMeshData.TryMedianDepth(new float[] {.49f,.49f,.49f,.49f,.49f,.49f,.49f,.49f,.8f},out _,out _),"picking cannot bypass 0.5m minimum with one far outlier");
            Require(!SurfaceMeshData.TryMedianDepth(new float[] {0,0,0,0,0,.7f,.7f,.7f,.7f},out _,out _),"missing depth cannot create a target centre");
            Require(SurfaceMeshData.TryMedianDepth(new float[] {.7f,.7f,.7f,.7f,.7f,.7f,.7f,.7f,4.8f},out var picked,out _) && Mathf.Abs(picked-.7f)<1e-5,"single depth outlier does not override measured target surface");
            Require(!SurfaceMeshData.TryMedianDepth(new float[] {.6f,.6f,.6f,.6f,.9f,.9f,.9f,.9f,.9f},out _,out _),"depth discontinuity cannot select a mixed foreground-background centre");
            var volume=new TsdfVolume(0.4f,40); var frame=Plane(1f,false);
            volume.Integrate(frame); volume.Integrate(frame);
            Require(volume.Extract().Triangles.Count==0,"two depth observations cannot create a final surface");
            for (int i=0;i<10;i++) volume.Integrate(Plane(1f+(i%2==0 ? 0.006f : -0.006f),false));
            var mesh=volume.Extract();
            Require(mesh.Triangles.Count>300 && mesh.Vertices.Count>100,"fusion produces a continuous observed surface");
            float error=0;
            foreach (var p in mesh.Vertices) { error+=p.z*p.z; Require(Mathf.Abs(p.z)<0.006f,"unseen volume walls are not fabricated"); }
            error=Mathf.Sqrt(error/mesh.Vertices.Count);
            Require(error<0.003f,"alternating 6 mm synthetic noise is reduced by fusion");
            int initial=mesh.Vertices.Count;
            for (int i=0;i<20;i++) volume.Integrate(Plane(1,false));
            var repeated=volume.Extract();
            Require(repeated.Vertices.Count<=initial+80,"repeated views refine one volume instead of adding layers");
            Require(repeated.Colors.Count==repeated.Vertices.Count && repeated.Colors[0].r>=118 && repeated.Colors[0].g<=42,"vertex colour is captured from RGB");
            var shifted=new TsdfVolume(0.4f,64);
            for (int i=0;i<6;i++) shifted.Integrate(Plane(1,false));
            for (int i=0;i<16;i++) { var drifted=Plane(1,false); drifted.VolumeToCamera=Matrix4x4.Translate(new Vector3(0,0,1.012f)); shifted.Integrate(drifted); }
            var aligned=shifted.Extract(); double shiftError=0;
            foreach (var point in aligned.Vertices) shiftError+=Math.Abs(point.z);
            Require(aligned.Vertices.Count>0 && shiftError/aligned.Vertices.Count<0.003,"bounded frame alignment prevents a 12mm pose residual moving the established plane");
            var rgbCrop = Plane(1,false); rgbCrop.ColorHeight=120; rgbCrop.Rgb=new byte[80*120*3];
            for (int y=0;y<120;y++) for (int x=0;x<80;x++)
                rgbCrop.Rgb[(y*80+x)*3+(y>=39 && y<=81 ? 1 : 0)]=255;
            var colouredCrop=new TsdfVolume(0.4f,40);
            for (int i=0;i<4;i++) colouredCrop.Integrate(rgbCrop);
            var croppedColour=colouredCrop.Extract(); int green=0;
            foreach (var c in croppedColour.Colors) if (c.g>240 && c.r<15) green++;
            Require(green>croppedColour.Vertices.Count*0.95f,"depth uses the centred RGB crop, excluding bands beyond its field of view");
            var clipped=new TsdfVolume(0.4f,40);
            for (int i=0;i<4;i++) clipped.Integrate(Plane(1,true));
            var partial=clipped.Extract(); Require(partial.Vertices.Count>0,"observed half-frame produces a surface");
            foreach (var p in partial.Vertices) Require(p.x<0.01f,"outside-frame samples do not enter the volume");
            var tooClose=new TsdfVolume(0.4f,32);
            for (int i=0;i<4;i++) tooClose.Integrate(Plane(0.499f,false));
            Require(tooClose.Frames==0 && tooClose.Extract().Vertices.Count==0,"sub-0.5m input does not create a surface");
            var sphere=new TsdfVolume(0.4f,40);
            for (int view=0;view<12;view++) for (int observation=0;observation<3;observation++) sphere.Integrate(Sphere(view*30));
            var curved=sphere.Extract(); Require(curved.Vertices.Count>200,"multi-view curved objects reconstruct beyond a plane");
            double curvedError=0;
            foreach (var p in curved.Vertices) curvedError+=Math.Abs(p.magnitude-0.12f);
            Require(curvedError/curved.Vertices.Count<0.012,"synthetic sphere stays near its known surface");
            var unityMesh=new Mesh(); curved.WriteTo(unityMesh);
            Require(unityMesh.vertexCount==curved.Vertices.Count && unityMesh.triangles.Length==curved.Triangles.Count,"mesh transfer preserves geometry");
            UnityEngine.Object.DestroyImmediate(unityMesh);
            string saved=SurfaceScanStore.Save(temporary,repeated,0.4f,volume.Frames); var restored=SurfaceScanStore.Load(saved);
            Require(restored.Frames==volume.Frames && restored.Geometry.Vertices.Count==repeated.Vertices.Count
                && restored.Geometry.Colors[0].Equals(repeated.Colors[0]) && File.Exists(Path.ChangeExtension(saved,"obj")),"offline roundtrip preserves model and colour with OBJ export");
            var bytes=File.ReadAllBytes(saved); File.WriteAllBytes(saved,new byte[] {1,2,3});
            bool invalid=false; try { SurfaceScanStore.Load(saved); } catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidDataException) { invalid=true; }
            Require(invalid,"corrupt models fail closed"); File.WriteAllBytes(saved,bytes);
            Debug.Log("SURFACE_SCAN_CHECKS_PASSED syntheticPlaneRms="+error+" sphereMeanError="+(curvedError/curved.Vertices.Count)); EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        finally
        {
            if (Directory.Exists(temporary)) { foreach (string path in Directory.GetFiles(temporary)) File.Delete(path); Directory.Delete(temporary); }
        }
    }
    private static void Coordinates()
    {
        var p=SurfaceMeshData.Unproject(49.5f,49.5f,2,100,100,new Vector2(100,100),new Vector2(50,50),new Vector2Int(100,100));
        Require((p-new Vector3(0,0,2)).magnitude<1e-5f,"calibrated optical axis is preserved");
        var crop=SurfaceMeshData.Unproject(79.5f,64.5f,1,160,90,new Vector2(400,400),new Vector2(320,240),new Vector2Int(640,480));
        Require(Mathf.Abs(crop.y+0.2f)<1e-5f,"16:9 depth is a centred crop of 4:3 intrinsics, not vertically stretched");
        var portrait=SurfaceMeshData.NativeCameraToWorld(Matrix4x4.identity,ScreenOrientation.Portrait);
        Require((portrait.MultiplyVector(Vector3.right)-Vector3.down).magnitude<1e-5f,"portrait axes match displayed camera");
        var upside=SurfaceMeshData.NativeCameraToWorld(Matrix4x4.Translate(Vector3.one),ScreenOrientation.PortraitUpsideDown);
        Require((upside.MultiplyPoint3x4(Vector3.right)-new Vector3(1,2,1)).magnitude<1e-5f,"screen orientation preserves translation");
    }
    private static TsdfVolume.Frame Plane(float depth,bool half)
    {
        var frame=Base(); frame.VolumeToCamera=Matrix4x4.Translate(Vector3.forward);
        for (int y=0;y<80;y++) for (int x=0;x<80;x++) { int i=y*80+x; frame.Depth[i]=depth; frame.Mask[i]=!half||x<40; }
        return frame;
    }
    private static TsdfVolume.Frame Sphere(float degrees)
    {
        var frame=Base(); float angle=degrees*Mathf.Deg2Rad;
        var origin=new Vector3(Mathf.Sin(angle)*0.8f,0.08f,-Mathf.Cos(angle)*0.8f);
        var pose=Matrix4x4.TRS(origin,Quaternion.LookRotation(-origin),Vector3.one); frame.VolumeToCamera=pose.inverse;
        for (int y=0;y<80;y++) for (int x=0;x<80;x++)
        {
            var ray=pose.MultiplyVector(new Vector3((x-39.5f)/100,-(y-39.5f)/100,1));
            float a=Vector3.Dot(ray,ray), b=2*Vector3.Dot(origin,ray), c=origin.sqrMagnitude-0.12f*0.12f;
            float discriminant=b*b-4*a*c; if (discriminant<0) continue;
            int i=y*80+x; frame.Depth[i]=(-b-Mathf.Sqrt(discriminant))/(2*a); frame.Mask[i]=true;
        }
        return frame;
    }
    private static TsdfVolume.Frame Base()
    {
        var frame=new TsdfVolume.Frame { Width=80,Height=80,Depth=new float[6400],Mask=new bool[6400],Confidence=new byte[6400],
            Focal=new Vector2(100,100),Principal=new Vector2(39.5f,39.5f),ColorWidth=80,ColorHeight=80,Rgb=new byte[6400*3] };
        for (int i=0;i<6400;i++) { frame.Confidence[i]=255; frame.Rgb[i*3]=120; frame.Rgb[i*3+1]=40; frame.Rgb[i*3+2]=30; }
        return frame;
    }
    private static void Require(bool condition,string description) { if (!condition) throw new Exception(description); }
}

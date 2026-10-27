using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class SurfaceScanStore
{
    private const string Magic = "ARWORK-SURFACE-1";
    public sealed class SavedScan { public string Path; public float Side; public int Frames; public SurfaceMeshData Geometry; }
    public static string DirectoryPath => System.IO.Path.Combine(Application.persistentDataPath, "surface-scans-v1");
    public static string Save(string directory, SurfaceMeshData geometry, float side, int frames)
    {
        if (geometry == null || geometry.Vertices.Count < 3 || geometry.Triangles.Count < 3) throw new ArgumentException("没有可保存的表面");
        Directory.CreateDirectory(directory);
        string stem = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,6);
        string path=System.IO.Path.Combine(directory,stem+".arscan"), temporary=path+".tmp";
        try
        {
            using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(Magic); writer.Write(side); writer.Write(frames);
                writer.Write(geometry.Vertices.Count); writer.Write(geometry.Triangles.Count);
                for (int i=0;i<geometry.Vertices.Count;i++)
                {
                    var point=geometry.Vertices[i]; writer.Write(point.x); writer.Write(point.y); writer.Write(point.z);
                    var color=geometry.Colors.Count==geometry.Vertices.Count ? geometry.Colors[i] : new Color32(80,190,190,255);
                    writer.Write(color.r); writer.Write(color.g); writer.Write(color.b);
                }
                foreach (int index in geometry.Triangles) writer.Write(index);
                writer.Flush(); file.Flush(true);
            }
            File.Move(temporary,path);
            WriteObj(System.IO.Path.ChangeExtension(path,"obj"),geometry);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static SavedScan Load(string path)
    {
        using (var file=File.OpenRead(path)) using (var reader=new BinaryReader(file))
        {
            if (file.Length > 32*1024*1024 || reader.ReadString()!=Magic) throw new InvalidDataException("扫描文件格式不正确");
            float side=reader.ReadSingle(); int frames=reader.ReadInt32(), count=reader.ReadInt32(), indices=reader.ReadInt32();
            if (!Finite(side) || side<0.1f || side>2 || frames<0 || count<3 || count>SurfaceMeshData.MaxVertices+8
                || indices<3 || indices>SurfaceMeshData.MaxTriangles*3+12 || indices%3!=0) throw new InvalidDataException("扫描文件超出支持范围");
            var mesh=new SurfaceMeshData(side/2);
            for (int i=0;i<count;i++)
            {
                var p=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                if (!Finite(p.x)||!Finite(p.y)||!Finite(p.z)||Mathf.Abs(p.x)>side||Mathf.Abs(p.y)>side||Mathf.Abs(p.z)>side)
                    throw new InvalidDataException("扫描文件包含非法坐标");
                mesh.Vertices.Add(p); mesh.Colors.Add(new Color32(reader.ReadByte(),reader.ReadByte(),reader.ReadByte(),255));
            }
            for (int i=0;i<indices;i++) { int index=reader.ReadInt32(); if (index<0||index>=count) throw new InvalidDataException("扫描网格损坏"); mesh.Triangles.Add(index); }
            if (file.Position!=file.Length) throw new InvalidDataException("扫描文件长度不正确");
            return new SavedScan { Path=path, Side=side, Frames=frames, Geometry=mesh };
        }
    }
    private static bool Finite(float value) => !float.IsNaN(value)&&!float.IsInfinity(value);
    private static void WriteObj(string path, SurfaceMeshData mesh)
    {
        string temporary=path+".tmp";
        try
        {
            using (var writer=new StreamWriter(temporary,false,System.Text.Encoding.UTF8))
            {
                writer.WriteLine("# AR-work surface, units: metres, right-handed coordinates, vertex RGB");
                for (int i=0;i<mesh.Vertices.Count;i++)
                {
                    var p=mesh.Vertices[i]; var c=mesh.Colors.Count==mesh.Vertices.Count ? mesh.Colors[i] : new Color32(80,190,190,255);
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,"v {0:R} {1:R} {2:R} {3:F4} {4:F4} {5:F4}",p.x,p.y,-p.z,c.r/255f,c.g/255f,c.b/255f));
                }
                for (int i=0;i<mesh.Triangles.Count;i+=3)
                    writer.WriteLine("f "+(mesh.Triangles[i]+1)+" "+(mesh.Triangles[i+2]+1)+" "+(mesh.Triangles[i+1]+1));
            }
            File.Move(temporary,path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

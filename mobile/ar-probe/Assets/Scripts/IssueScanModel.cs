using System;
using UnityEngine;
[Serializable] public sealed class IssueScanModel
{
 public int version=1;
 public float[] vertices; public int[] colors, triangles;
 [Serializable] public sealed class Marker {public IssueHistoryPanel.Position position, normal;}
 public Marker marker;
 private static IssueHistoryPanel.Position Point(Vector3 p) => new IssueHistoryPanel.Position{x=p.x,y=p.y,z=-p.z};
 public static IssueScanModel Freeze(SurfaceMeshData data, Matrix4x4 worldToLocal, Vector3 point, Vector3 normal)
 {
  if(data==null||data.Vertices.Count<3||data.Colors.Count!=data.Vertices.Count||data.Triangles.Count<3) throw new InvalidOperationException("请完成带颜色的局部扫描并重新选点。");
  var model=new IssueScanModel{vertices=new float[data.Vertices.Count*3],colors=new int[data.Vertices.Count*3],triangles=data.Triangles.ToArray()};
  for(int i=0;i<data.Vertices.Count;i++){var p=data.Vertices[i];var c=data.Colors[i];int j=i*3;model.vertices[j]=p.x;model.vertices[j+1]=p.y;model.vertices[j+2]=-p.z;model.colors[j]=c.r;model.colors[j+1]=c.g;model.colors[j+2]=c.b;}
  for(int i=0;i<model.triangles.Length;i+=3){int b=model.triangles[i+1];model.triangles[i+1]=model.triangles[i+2];model.triangles[i+2]=b;}
  model.marker=new Marker{position=Point(worldToLocal.MultiplyPoint3x4(point)),normal=Point(worldToLocal.MultiplyVector(normal).normalized)};return model;
 }
}

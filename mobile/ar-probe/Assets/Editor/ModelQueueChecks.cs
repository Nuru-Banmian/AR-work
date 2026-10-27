using System;
using System.IO;
using UnityEditor;
using UnityEngine;
public static class ModelQueueChecks {
 public static void Run(){string root=Path.Combine(Path.GetTempPath(),"issue24-"+Guid.NewGuid().ToString("N"));try{
  var data=new SurfaceMeshData(1);data.Vertices.AddRange(new[]{Vector3.zero,Vector3.right,Vector3.up});data.Colors.AddRange(new[]{new Color32(255,0,0,255),new Color32(0,255,0,255),new Color32(0,0,255,255)});data.Triangles.AddRange(new[]{0,1,2});
  var matrix=Matrix4x4.TRS(new Vector3(3,4,5),Quaternion.Euler(0,90,0),Vector3.one);
  var m=IssueScanModel.Freeze(data,matrix.inverse,matrix.MultiplyPoint3x4(new Vector3(0.25f,0.25f,0)),matrix.MultiplyVector(Vector3.forward));
  Check(Mathf.Abs(m.marker.position.x-0.25f)<0.0001f&&Mathf.Abs(m.marker.position.y-0.25f)<0.0001f&&Mathf.Abs(m.marker.position.z)<0.0001f,"local marker translation and rotation");
  Check(Mathf.Abs(m.marker.normal.z+1)<0.0001f&&m.triangles[1]==2&&m.triangles[2]==1,"right handed normal and winding");
  var body=JsonUtility.ToJson(new ReportPayload{reportVersion=2,model=m,snapshot="frozen-photo",title="one",priority="high",position=new IssueHistoryPanel.Position()});
  var queue=new ReportQueue(root);queue.Add("http://127.0.0.1:18121","/api/reports",body,"model-key",true);
  data.Vertices.Clear();data.Colors.Clear();m.vertices[0]=9;
  queue=new ReportQueue(root);Check(queue.Get("model-key").body==body,"restart preserves independent model and photo after scanning changes");
  bool blocked=false;try{queue.Edit("model-key","changed");}catch(InvalidOperationException){blocked=true;}Check(blocked,"new report immutable before first network attempt");
  queue.BeginSend("model-key");queue.Fail("model-key","lost receipt");queue=new ReportQueue(root);Check(queue.BeginSend("model-key").body==body,"failure and restart retry exact model and key");
  queue.Add("http://127.0.0.1:18121","/api/reports","{\"snapshot\":\"old\"}","legacy-key",true);Check(queue.Get("legacy-key").body.Contains("old"),"legacy model-free request retained");
  Debug.Log("ISSUE24_MODEL_QUEUE_PASSED");EditorApplication.Exit(0);
 }catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}finally{if(Directory.Exists(root))Directory.Delete(root,true);}}
 static void Check(bool value,string name){if(!value)throw new Exception(name);Debug.Log("ISSUE24_PASS="+name);}
}

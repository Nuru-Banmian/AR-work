using System;
using System.IO;
using UnityEngine;

// Freeze the exact request before its first send. A failed write never starts HTTP.
internal static class PendingReportStore
{
    private static string PathName => Path.Combine(Application.persistentDataPath, "pending-report.json");
    public static string Read() => File.Exists(PathName) ? File.ReadAllText(PathName) : null;
    public static void Save(string json)
    {
        var temporary = PathName + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) { writer.Write(json); writer.Flush(); stream.Flush(true); }
            if (File.Exists(PathName)) File.Replace(temporary, PathName, null);
            else File.Move(temporary, PathName);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Delete() { if (File.Exists(PathName)) File.Delete(PathName); }
}

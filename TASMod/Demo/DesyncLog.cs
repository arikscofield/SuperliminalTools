#if DEBUG_PROBES
// TASMod/Demo/DesyncLog.cs
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SuperliminalTools.TASMod.Demo;

internal static class DesyncLog
{
    private static StreamWriter _w;

    private static string B(float f)
    {
        var b = BitConverter.GetBytes(f);
        return BitConverter.ToInt32(b, 0).ToString("X8");
    }

    private static string B(Vector3 v) => $"{B(v.x)}:{B(v.y)}:{B(v.z)}";
    private static string B(Quaternion q) => $"{B(q.x)}:{B(q.y)}:{B(q.z)}:{B(q.w)}";

    public static void Start(string tag)
    {
        Stop();
        var dir = Path.Combine(Application.dataPath, "..", "demos", "desync");
        Directory.CreateDirectory(dir);
        _w = new StreamWriter(Path.Combine(dir, $"{tag}_{Environment.MachineName}.log"));
        _w.WriteLine($"# cpu={SystemInfo.processorType} cores={SystemInfo.processorCount} game={Application.version}");
    }

    public static void Stop() { _w?.Flush(); _w?.Dispose(); _w = null; }

    public static void Frame(int frame)
    {
        if (_w == null) return;
        var sb = new StringBuilder(512);
        sb.Append(frame).Append('|');

        var p = GameManager.GM != null ? GameManager.GM.player : null;
        sb.Append(p != null ? B(p.transform.position) : "-").Append('|');

        var cam = Camera.main;
        sb.Append(cam != null ? B(cam.transform.rotation) : "-").Append('|');

        foreach (var rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
        {
            if (rb == null || rb.IsSleeping()) continue;
            sb.Append(rb.name).Append(':').Append(B(rb.position))
                .Append(':').Append(B(rb.velocity)).Append(';');
        }

        _w.WriteLine(sb.ToString());
    }
}
#endif

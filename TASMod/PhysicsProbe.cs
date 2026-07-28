#if DEBUG_PROBES
// TASMod/PhysicsProbe.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SuperliminalTools.TASMod;

internal static class PhysicsProbe
{
    private static int _runIndex;

    private static bool _reflectionTried;
    private static bool _localScenes;
    private static MethodInfo _getPhysicsScene;
    private static MethodInfo _simulate;

    private static void InitReflection()
    {
        if (_reflectionTried) return;
        _reflectionTried = true;

        Type ext = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { ext = a.GetType("UnityEngine.PhysicsSceneExtensions", false); }
            catch { ext = null; }
            if (ext != null) { Debug.Log($"PhysicsProbe: found PhysicsSceneExtensions in {a.GetName().Name}"); break; }
        }
        if (ext == null)
        {
            Debug.LogWarning("PhysicsProbe: PhysicsSceneExtensions not found. Candidates:");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name.IndexOf("PhysicsScene", StringComparison.Ordinal) >= 0 ||
                        t.Name.IndexOf("LocalPhysicsMode", StringComparison.Ordinal) >= 0 ||
                        t.Name.IndexOf("CreateSceneParameters", StringComparison.Ordinal) >= 0)
                        Debug.LogWarning($"   {a.GetName().Name} :: {t.FullName}");
            }
            Debug.LogWarning($"   Rigidbody is defined in: {typeof(Rigidbody).Assembly.GetName().Name}");
            return;
        }

        foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == "GetPhysicsScene" && m.GetParameters().Length == 1) { _getPhysicsScene = m; break; }
        if (_getPhysicsScene == null)
        {
            Debug.LogWarning("PhysicsProbe: GetPhysicsScene not found; using frozen-world mode");
            return;
        }

        foreach (var m in _getPhysicsScene.ReturnType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "Simulate" && m.GetParameters().Length == 1) { _simulate = m; break; }

        _localScenes = _simulate != null;
        Debug.Log($"PhysicsProbe: local physics scenes {(_localScenes ? "available" : "unavailable")}");
    }

    private static string B(float f)
    {
        var b = BitConverter.GetBytes(f);
        return BitConverter.ToInt32(b, 0).ToString("X8");
    }

    private static string B(Vector3 v) => $"{B(v.x)}:{B(v.y)}:{B(v.z)}";
    private static string B(Quaternion q) => $"{B(q.x)}:{B(q.y)}:{B(q.z)}:{B(q.w)}";

    private static GameObject Box(Vector3 pos, Vector3 scale, Quaternion rot, bool dynamic)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
        go.transform.position = pos;
        go.transform.rotation = rot;
        go.transform.localScale = scale;
        if (dynamic)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1.3f;
            rb.useGravity = true;
            rb.velocity = new Vector3(0.37f, -1.1f, 0.73f);
            rb.angularVelocity = new Vector3(1.7f, -0.3f, 2.1f);
        }
        return go;
    }

    private static string RunOnce(int steps, float dt)
    {
        var sb = new StringBuilder(1 << 18);
        var spawned = new List<GameObject>();
        var frozen = new List<Rigidbody>();

        Scene scene = default;
        object phys = null;
        bool prevAuto = Physics.autoSimulation;

        try
        {
            if (_localScenes)
            {
                scene = SceneManager.CreateScene(
                    "physprobe_" + Guid.NewGuid().ToString("N"),
                    new CreateSceneParameters { localPhysicsMode = LocalPhysicsMode.Physics3D });
                phys = _getPhysicsScene.Invoke(null, new object[] { scene });
            }
            else
            {
                // Freeze every live body so Simulate moves nothing but our rig.
                foreach (var rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                    if (!rb.isKinematic) { rb.isKinematic = true; frozen.Add(rb); }
                Physics.autoSimulation = false;
            }

            var o = new Vector3(0f, -5000f, 0f);
            spawned.Add(Box(o, new Vector3(60f, 1f, 60f), Quaternion.identity, false));
            spawned.Add(Box(o + new Vector3(7f, 3.1f, 0f), new Vector3(14f, 1f, 14f),
                            Quaternion.Euler(0f, 23f, 27f), false));   // ramp
            spawned.Add(Box(o + new Vector3(-9f, 2.7f, 4f), new Vector3(1f, 9f, 13f),
                            Quaternion.Euler(11f, 0f, 0f), false));    // wall

            var dyn = new List<Rigidbody>();
            for (int i = 0; i < 12; i++)
            {
                var p = o + new Vector3(
                    -3.3f + i * 0.91f,
                    9.7f + i * 4.5f,
                    1.1f + (i % 5) * 0.63f);
                var g = Box(p, Vector3.one * (0.7f + i * 0.11f),
                            Quaternion.Euler(i * 13.7f, i * 29.3f, i * 7.1f), true);
                spawned.Add(g);
                dyn.Add(g.GetComponent<Rigidbody>());
            }

            if (_localScenes)
                foreach (var g in spawned) SceneManager.MoveGameObjectToScene(g, scene);

            var arg = new object[1];
            for (int s = 0; s < steps; s++)
            {
                if (_localScenes) { arg[0] = dt; _simulate.Invoke(phys, arg); }
                else Physics.Simulate(dt);

                sb.Append(s).Append('|');
                for (int i = 0; i < dyn.Count; i++)
                    sb.Append(B(dyn[i].position)).Append(',')
                      .Append(B(dyn[i].rotation)).Append(',')
                      .Append(B(dyn[i].velocity)).Append(';');
                sb.AppendLine();
            }
        }
        finally
        {
            foreach (var g in spawned) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            foreach (var rb in frozen) if (rb != null) rb.isKinematic = false;
            if (_localScenes && scene.IsValid()) SceneManager.UnloadSceneAsync(scene);
            Physics.autoSimulation = prevAuto;
        }
        return sb.ToString();
    }

    public static void Run(int steps = 300)
    {
        InitReflection();

        var prevGravity = Physics.gravity;
        const float dt = 0.02f;

        string a, b;
        double elapsedMs;
        try
        {
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
            RunOnce(steps, dt);          // warm up: first-call JIT and PhysX allocation

            var sw = Stopwatch.StartNew();
            a = RunOnce(steps, dt);
            b = RunOnce(steps, dt);      // self-check
            sw.Stop();
            elapsedMs = sw.Elapsed.TotalMilliseconds;
        }
        finally
        {
            Physics.gravity = prevGravity;
        }

        var sb = new StringBuilder(a.Length + 512);
        sb.AppendLine($"# cpu       : {SystemInfo.processorType}");
        sb.AppendLine($"# cores     : {SystemInfo.processorCount}");
        sb.AppendLine($"# game      : {Application.version}");
        sb.AppendLine($"# grav      : {B(new Vector3(0f, -9.81f, 0f))}");
        sb.AppendLine($"# dt        : {B(dt)}");
        sb.AppendLine($"# mode      : {(_localScenes ? "localscene" : "frozen")}");
        sb.AppendLine($"# selfcheck : {(a == b ? "PASS" : "FAIL")}");
        sb.AppendLine($"# sim ms    : {elapsedMs:F2} for {steps * 2} steps ({elapsedMs / (steps * 2):F4} ms/step)");
        sb.AppendLine();
        sb.AppendLine("### RUN A");
        sb.Append(a);
        sb.AppendLine("### RUN B");
        sb.Append(b);

        var dir = Path.Combine(Application.dataPath, "..", "demos");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"physprobe_{Environment.MachineName}_{_runIndex++}.txt");
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"PhysicsProbe -> {path} (mode {(_localScenes ? "localscene" : "frozen")}, selfcheck {(a == b ? "PASS" : "FAIL")})");
    }
}
#endif

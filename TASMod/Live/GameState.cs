using System.Collections.Generic;
using MoonSharp.Interpreter;
using SuperliminalTools.TASMod.Demo;
using UnityEngine;

namespace SuperliminalTools.TASMod.Live;

/// <summary>
/// Read-only view of live game state, exposed to Lua scripts as the global `game`.
/// Angle conventions (defined once, here):
///   yaw   0 = facing +Z, increasing toward +X, wraps at 360.
///   pitch 0 = horizon, positive = looking up, range about -90..90.
/// </summary>
[MoonSharpUserData]
public sealed class GameState
{
    // Objects the script has selected via nearest(); index into this list is the "handle".
    private readonly List<Transform> _targets = new();

    private static GameObject Player => GameManager.GM != null ? GameManager.GM.player : null;
    private static Camera Cam => GameManager.GM != null ? GameManager.GM.playerCamera : null;

    // ---- simple scalars -------------------------------------------------

    public bool grounded()
    {
        var p = Player;
        if (p == null) return false;
        var motor = p.GetComponent<CharacterMotor>();
        return motor != null && motor.grounded;
    }

    public bool is_grabbing()
    {
        var rs = Cam != null ? Cam.GetComponent<ResizeScript>() : null;
        return rs != null && rs.isGrabbing;
    }

    public bool is_ready_to_grab()
    {
        var rs = Cam != null ? Cam.GetComponent<ResizeScript>() : null;
        return rs != null && rs.isReadyToGrab;
    }

    public int checkpoint_index()
    {
        return DemoRecorder.Instance != null ? DemoRecorder.Instance.CurrentCheckpointIndex() : -1;
    }

    // ---- vectors (returned as multiple Lua values: x, y, z) -------------

    public DynValue player_pos()
    {
        var p = Player;
        var v = p != null ? p.transform.position : Vector3.zero;
        return Vec(v);
    }

    public DynValue velocity()
    {
        var p = Player;
        var cc = p != null ? p.GetComponent<CharacterController>() : null;
        return Vec(cc != null ? cc.velocity : Vector3.zero);
    }

    /// <summary>Horizontal speed only — handy for "wait until stopped".</summary>
    public double speed()
    {
        var p = Player;
        var cc = p != null ? p.GetComponent<CharacterController>() : null;
        if (cc == null) return 0;
        var v = cc.velocity;
        return Mathf.Sqrt(v.x * v.x + v.z * v.z);
    }

    /// <summary>Current camera facing as yaw, pitch (degrees, conventions above).</summary>
    public DynValue facing()
    {
        var (yaw, pitch) = CurrentFacing();
        return DynValue.NewTuple(DynValue.NewNumber(yaw), DynValue.NewNumber(pitch));
    }
    
    /// <summary>
    /// Degrees of rotation produced per 1.0 of Look axis, for the mouse path:
    ///   yaw_degrees   = look_h * scale.x
    ///   pitch_degrees = look_v * scale.y
    /// Reads the live MouseLook sensitivity and folds in the invert-axis prefs, so
    /// a script can turn a desired angle (in degrees) straight into a look delta.
    /// Returns 0 for an axis whose MouseLook isn't present (script guards against it).
    /// </summary>
    public DynValue look_scale()
    {
        float sx = 0f, sy = 0f;
        var p = Player;
        var c = Cam;
        var yawLook = p != null ? p.GetComponent<MouseLook>() : null;    // player: MouseX / yaw
        var pitchLook = c != null ? c.GetComponent<MouseLook>() : null;  // camera: MouseY / pitch
        if (yawLook != null) sx = yawLook.sensitivityX * InvertAxis.GetInvertXAxisMultiplier();
        if (pitchLook != null) sy = pitchLook.sensitivityY * InvertAxis.GetInvertYAxisMultiplier();
        return DynValue.NewTuple(DynValue.NewNumber(sx), DynValue.NewNumber(sy));
    }

    // ---- target selection ----------------------------------------------
    
    
    // helper to aim at the collider/renderer
    // bounds center — that's what "look at the object" should mean.
    private static Vector3 AimCenter(Transform t)
    {
        if (t == null) return Vector3.zero;
        var col = t.GetComponentInChildren<Collider>();
        if (col != null) return col.bounds.center;
        var rend = t.GetComponentInChildren<Renderer>();
        if (rend != null) return rend.bounds.center;
        return t.position;
    }
    
    /// <summary>
    /// Names + distances of nearby grabbables, nearest first. Call it from a script
    /// (tas.grabbables()) to discover what to pass to nearest("..."). Logs and returns.
    /// </summary>
    public string list_grabbables(int max = 15)
    {
        var p = Player;
        if (p == null) return "";
        var origin = p.transform.position;

        var items = new List<(string name, float d)>();
        foreach (var dt in Object.FindObjectsOfType<DropTriggerScript>())
            items.Add((dt.name, Vector3.Distance(AimCenter(dt.transform), origin)));
        items.Sort((a, b) => a.d.CompareTo(b.d));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"grabbables ({items.Count}):");
        for (int i = 0; i < items.Count && i < max; i++)
            sb.AppendLine($"  {items[i].name}  ({items[i].d:0.0}m)");
        var s = sb.ToString();
        Debug.Log(s);   // surfaces in the Unity/player log
        return s;
    }

    /// <summary>
    /// Nearest GRABBABLE object (has a DropTriggerScript — the game's own grab
    /// marker; these sit on the "CanGrab" layer), optionally filtered by a
    /// case-insensitive name substring. Returns a handle (>= 0) or -1.
    /// </summary>
    public int nearest(string nameFilter = null)
    {
        var p = Player;
        if (p == null) return -1;
        var origin = p.transform.position;

        Transform best = null;
        float bestSq = float.MaxValue;
        bool wildcard = string.IsNullOrEmpty(nameFilter) || nameFilter == "*";
        string needle = wildcard ? null : nameFilter.ToLowerInvariant();

        foreach (var dt in Object.FindObjectsOfType<DropTriggerScript>())
        {
            var t = dt.transform;
            if (!wildcard && !t.name.ToLowerInvariant().Contains(needle)) continue;
            float sq = (AimCenter(t) - origin).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = t; }
        }

        if (best == null) return -1;
        _targets.Add(best);
        return _targets.Count - 1;
    }

    public DynValue target_pos(int handle)
    {
        var t = Resolve(handle);
        return Vec(t != null ? AimCenter(t) : Vector3.zero);
    }

    public double target_distance(int handle)
    {
        var t = Resolve(handle);
        var p = Player;
        if (t == null || p == null) return -1;
        return Vector3.Distance(p.transform.position, AimCenter(t));
    }

    // ---- aim errors (yaw_err, pitch_err in degrees) ---------------------
    // Positive yaw_err  = target is to your yaw-increasing side.
    // Positive pitch_err = target is above where you're looking.
    // How those map to Look axis signs is game-defined, so the Lua control
    // loop has yaw_sign / pitch_sign knobs — flip them if aiming diverges.

    public DynValue aim_error(int handle)
    {
        var t = Resolve(handle);
        if (t == null) return Err(0, 0);
        return AimErrorToPoint(AimCenter(t));
    }

    public DynValue aim_error_point(double x, double y, double z)
        => AimErrorToPoint(new Vector3((float)x, (float)y, (float)z));

    /// <summary>Error toward an absolute world direction given as yaw, pitch (degrees).</summary>
    public DynValue aim_error_dir(double yaw, double pitch)
    {
        var eye = Cam != null ? Cam.transform.position : Vector3.zero;
        return AimErrorToPoint(eye + DirFromAngles((float)yaw, (float)pitch) * 1000f);
    }

    // ---- helpers --------------------------------------------------------

    private Transform Resolve(int handle)
        => handle >= 0 && handle < _targets.Count ? _targets[handle] : null;

    private static DynValue Vec(Vector3 v) => DynValue.NewTuple(
        DynValue.NewNumber(v.x), DynValue.NewNumber(v.y), DynValue.NewNumber(v.z));

    private static DynValue Err(double yaw, double pitch) => DynValue.NewTuple(
        DynValue.NewNumber(yaw), DynValue.NewNumber(pitch));

    private static (float yaw, float pitch) CurrentFacing()
    {
        var c = Cam;
        var fwd = c != null ? c.transform.forward : Vector3.forward;
        return (YawOf(fwd), PitchOf(fwd));
    }

    private DynValue AimErrorToPoint(Vector3 point)
    {
        var eye = Cam != null ? Cam.transform.position : Vector3.zero;
        Vector3 dir = point - eye;
        if (dir.sqrMagnitude < 1e-6f) return Err(0, 0);

        var (curYaw, curPitch) = CurrentFacing();
        float yawErr = Mathf.DeltaAngle(curYaw, YawOf(dir)); // shortest signed turn
        float pitchErr = PitchOf(dir) - curPitch;            // pitch never wraps
        return Err(yawErr, pitchErr);
    }

    private static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

    private static float PitchOf(Vector3 dir)
    {
        Vector3 n = dir.normalized;
        return Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    private static Vector3 DirFromAngles(float yaw, float pitch)
    {
        // Reverse of YawOf/PitchOf.
        float y = Mathf.Sin(pitch * Mathf.Deg2Rad);
        float h = Mathf.Cos(pitch * Mathf.Deg2Rad);
        float x = h * Mathf.Sin(yaw * Mathf.Deg2Rad);
        float z = h * Mathf.Cos(yaw * Mathf.Deg2Rad);
        return new Vector3(x, y, z);
    }
}
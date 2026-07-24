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

    // ---- target selection ----------------------------------------------

    /// <summary>
    /// Nearest grabbable object, optionally filtered by a case-insensitive name substring.
    /// Grabbables in Superliminal have a Rigidbody, so that's our candidate set.
    /// Returns a handle (>= 0) or -1 if nothing matched. Nearest-by-distance is
    /// independent of enumeration order, so it stays deterministic.
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

        foreach (var body in Object.FindObjectsOfType<Rigidbody>())
        {
            if (!wildcard && !body.name.ToLowerInvariant().Contains(needle)) continue;
            float sq = (body.transform.position - origin).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = body.transform; }
        }

        if (best == null) return -1;
        _targets.Add(best);
        return _targets.Count - 1;
    }

    public DynValue target_pos(int handle)
    {
        var t = Resolve(handle);
        return Vec(t != null ? t.position : Vector3.zero);
    }

    public double target_distance(int handle)
    {
        var t = Resolve(handle);
        var p = Player;
        if (t == null || p == null) return -1;
        return Vector3.Distance(p.transform.position, t.position);
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
        return AimErrorToPoint(t.position);
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
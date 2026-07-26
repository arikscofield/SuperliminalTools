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
    
    private static CharacterMotor Motor => Player != null ? Player.GetComponent<CharacterMotor>() : null;
    
    private static ResizeScript RS => Cam != null ? Cam.GetComponent<ResizeScript>() : null;
    
    private double _aimLead;

    // ---- simple scalars -------------------------------------------------

    public bool grounded()
    {
        var p = Player;
        if (p == null) return false;
        var motor = p.GetComponent<CharacterMotor>();
        return motor != null && motor.grounded;
    }
    
    //Seconds left on the jump cooldown (CharacterMotor.timeOnGroundBeforeCanJump)
    public double jump_cooldown()
    {
        var m = Motor;
        return m != null ? Mathf.Max(0f, m.timeOnGroundBeforeCanJump) : 0.0;
    }

    // Frames left on the cooldown at the fixed timestep
    public int jump_cooldown_frames()
        => Mathf.CeilToInt((float)jump_cooldown() / Time.fixedDeltaTime);
    
    // True when a Jump press will actually produce upward velocity
    public bool can_jump()
    {
        var m = Motor;
        return m != null && m.grounded && m.jumping.enabled && m.timeOnGroundBeforeCanJump <= 0f;
    }

    public bool is_grabbing()
    {
        var rs = Cam != null ? Cam.GetComponent<ResizeScript>() : null;
        return rs != null && rs.isGrabbing;
    }
    
    private static GameObject Grabbed()
    {
        var rs = RS;
        if (rs == null || !rs.isGrabbing) return null;
#if LEGACY
        return rs.grabbedObject;
#else
        return rs.GetGrabbedObject();
#endif
    }

    public bool is_ready_to_grab()
    {
        var rs = Cam != null ? Cam.GetComponent<ResizeScript>() : null;
        return rs != null && rs.isReadyToGrab;
    }
    
    // True when a spinnable object is in hand (Rotate would spin it)
    public bool can_spin()
    {
        var g = Grabbed();
        var dts = g != null ? g.GetComponent<DropTriggerScript>() : null;
        return dts != null && dts.grabValues != null && dts.grabValues.canSpin;
    }
    
    /// <summary>
    /// Degrees of world-up spin the held object gets per 1.0 of Look Horizontal, for a
    /// frame like the last one. Folds in the invert pref, the camera pitch term, the
    /// y-axis-only special case and Time.deltaTime, so Lua turns a wanted angle into a
    /// look delta with one divide. 0 when nothing spinnable is held.
    /// Sign note: positive spin (yaw increasing) comes from NEGATIVE Look Horizontal.
    /// </summary>
    public double spin_scale()
    {
        var g = Grabbed();
        var dts = g != null ? g.GetComponent<DropTriggerScript>() : null;
        if (dts == null || dts.grabValues == null || !dts.grabValues.canSpin) return 0.0;

        float invX = 1f;
#if HAS_INVERT_MULTIPLIER
        invX = InvertAxis.GetInvertXAxisMultiplier();
#endif

        float dt = Time.deltaTime;
        if (dts.optionalSpinYAxisOnly) return -invX * 150f * dt;
        var c = Cam;
        float cosPitch = c != null ? c.transform.up.y : 1f;   // camera.TransformDirection(0,-h,0).y
        return -invX * 100f * dt * cosPitch;
    }
    
    // World-Y euler of the held object
    // Returns -999 when nothing is held. Wraps at 360
    public double grabbed_yaw()
    {
        var g = Grabbed();
        return g != null ? g.transform.eulerAngles.y : -999.0;
    }

    public string grabbed_name()
    {
        var g = Grabbed();
        return g != null ? g.name : "";
    }

    public int checkpoint_index()
    {
        return DemoRecorder.Instance != null ? DemoRecorder.Instance.CurrentCheckpointIndex() : -1;
    }
    
    // Instantly move the player to a checkpoint (no reload).
    // lands at the end of the frame it's requested on
    public void warp_to_checkpoint(int index)
    {
        if (DemoRecorder.Instance != null) DemoRecorder.Instance.RequestCheckpointWarp(index);
    }

    // How many checkpoints this level has
    public int checkpoint_count()
        => Object.FindObjectsOfType<CheckPoint>().Length;

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
        #if HAS_INVERT_MULTIPLIER
            if (yawLook != null) sx = yawLook.sensitivityX * InvertAxis.GetInvertXAxisMultiplier();
            if (pitchLook != null) sy = pitchLook.sensitivityY * InvertAxis.GetInvertYAxisMultiplier();
        #elif LEGACY
            var psm = GameManager.GM != null ? GameManager.GM.GetComponent<PlayerSettingsManager>() : null;
            bool invertY = psm != null && psm.GetInvertYAxis();
            if (yawLook != null) sx = yawLook.sensitivityX;
            if (pitchLook != null) sy = pitchLook.sensitivityY * (invertY ? -1 : 1);
        #else
            if (yawLook != null) sx = yawLook.sensitivityX;
            if (pitchLook != null) sy = pitchLook.sensitivityY * (InvertAxis.GetInvertYAxis() ? -1 : 1);
        #endif
        return DynValue.NewTuple(DynValue.NewNumber(sx), DynValue.NewNumber(sy));
    }
    
    
    /// <summary>
    /// Local move axes (x = strafe, y = forward) that produce world heading `yaw`
    /// at `speed` fraction of max. Inverts the exact transform FPSInputController
    /// applies (player rotation, not camera) and pre-compensates its magnitude
    /// squaring, so no angle bookkeeping is left in Lua to get wrong.
    /// <summary>
    public DynValue move_axes_for(double yaw, double speed)
    {
        var p = Player;
        if (p == null) return Err(0, 0);

        Vector3 world = DirFromAngles((float)yaw, 0f);
        Vector3 local = Quaternion.Inverse(p.transform.rotation) * world;
        local.y = 0f;
        local = local.normalized;

        float m = Mathf.Sqrt(Mathf.Clamp01((float)speed));
        return Err(local.x * m, local.z * m);
    }

    //Player transform yaw - the rotaiton movement is actually built from
    public double player_yaw()
    {
        var p = Player;
        return p != null ? YawOf(p.transform.forward) : 0.0;
    }

    // World heading we're actually travelling, or -999 if stopped
    public double travel_yaw()
    {
        var p = Player;
        var cc = p != null ? p.GetComponent<CharacterController>() : null;
        if (cc == null) return -999.0;
        var v = cc.velocity;
        if (v.x * v.x + v.z * v.z < 0.01f) return -999.0;
        return YawOf(new Vector3(v.x, 0f, v.z));
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
    /// The `index`-th nearest GRABBABLE object (1 = nearest), optionally filtered
    /// by name. The filter is a case-insensitive substring, or an exact name if
    /// you prefix it with '='
    /// Returns a handle (>= 0) or -1.
    /// </summary>
    public int nearest(string nameFilter = null, int index = 1)
    {
        var p = Player;
        if (p == null || index < 1) return -1;
        var origin = p.transform.position;

        bool wildcard = string.IsNullOrEmpty(nameFilter) || nameFilter == "*";
        bool exact = !wildcard && nameFilter[0] == '=';
        string needle = wildcard ? null
            : (exact ? nameFilter.Substring(1) : nameFilter.ToLowerInvariant());

        var matches = new List<Transform>();
        foreach (var dt in Object.FindObjectsOfType<DropTriggerScript>())
        {
            var t = dt.transform;
            if (!wildcard)
            {
                if (exact) { if (t.name != needle) continue; }
                else if (!t.name.ToLowerInvariant().Contains(needle)) continue;
            }
            matches.Add(t);
        }

        if (matches.Count < index) return -1;
        matches.Sort((a, b) => (AimCenter(a) - origin).sqrMagnitude
            .CompareTo((AimCenter(b) - origin).sqrMagnitude));

        _targets.Add(matches[index - 1]);
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
    
    public void set_aim_lead(double frames) { _aimLead = frames; }

    private Vector3 Eye()
    {
        var c = Cam;
        if (c == null) return Vector3.zero;
        var eye = c.transform.position;
        if (_aimLead != 0.0)
        {
            var p = Player;
            var cc = p != null ? p.GetComponent<CharacterController>() : null;
            if (cc != null) eye += cc.velocity * (float)(_aimLead * Time.fixedDeltaTime);
        }
        return eye;
    }

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
        // var eye = Cam != null ? Cam.transform.position : Vector3.zero;
        var eye = Eye();
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
        // var eye = Cam != null ? Cam.transform.position : Vector3.zero;
        var eye = Eye();
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
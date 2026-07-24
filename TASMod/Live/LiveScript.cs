using System;
using System.Collections.Generic;
using System.IO;
using MoonSharp.Interpreter;
using UnityEngine;

namespace SuperliminalTools.TASMod.Live;

/// <summary>
/// Runs a user Lua script live: resumes it one frame at a time, exposing the
/// per-frame inputs it produces. The script pauses itself (coroutine.yield)
/// once per frame from inside the Lua LiveSink, and we resume it here.
/// </summary>
public sealed class LiveScript
{
    private static readonly string[] AxisNames =
        { "Move Horizontal", "Move Vertical", "Look Horizontal", "Look Vertical" };
    private static readonly string[] ButtonNames = { "Jump", "Grab", "Rotate" };

    // A minimal require backed by modules we preload from C#. Avoids MoonSharp's
    // module-path resolution entirely (it doesn't do the init.lua convention).
    private const string RequireBootstrap = @"
        local __loaded = {}
        function require(name)
          local c = __loaded[name]; if c ~= nil then return c end
          local loader = __modules[name]
          if loader == nil then error('module not found: ' .. tostring(name)) end
          local m = loader(); if m == nil then m = true end
          __loaded[name] = m; return m
        end";

    private readonly Script _script;
    private readonly DynValue _co;
    private readonly GameState _game = new();

    private readonly Dictionary<string, float> _axis = new();
    private readonly Dictionary<string, bool> _cur = new();
    private readonly Dictionary<string, bool> _prev = new();

    public bool Finished { get; private set; }
    public int FrameCount { get; private set; }
    public float? Speed { get; private set; }
    public bool Reset { get; private set; }

    static LiveScript()
    {
        UserData.RegisterType<GameState>();
    }

    public LiveScript(string scriptPath, string scriptsRoot)
    {
        foreach (var a in AxisNames) _axis[a] = 0f;
        foreach (var b in ButtonNames) { _cur[b] = false; _prev[b] = false; }
        
        _script = new Script(CoreModules.Preset_SoftSandbox);
        _script.Globals["__tas_live"] = true;
        _script.Globals["__tas_game"] = UserData.Create(_game);

        PreloadTasModules(scriptsRoot);
        _script.DoString(RequireBootstrap);

        var code = File.ReadAllText(scriptPath);
        var fn = _script.LoadString(code, null, Path.GetFileName(scriptPath));
        _co = _script.CreateCoroutine(fn);
    }

    /// <summary>
    /// Compiles every tas/*.lua into a module chunk and registers it under its
    /// require name (init.lua -> "tas", frame.lua -> "tas.frame", etc.), so the
    /// user script's require("tas") / require("tas.frame") resolve without files.
    /// </summary>
    private void PreloadTasModules(string scriptsRoot)
    {
        var tasDir = Path.Combine(scriptsRoot, "tas");
        if (!Directory.Exists(tasDir))
            throw new FileNotFoundException($"tas module folder not found next to the script: {tasDir}");

        var modules = new Table(_script);
        foreach (var file in Directory.GetFiles(tasDir, "*.lua"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var modName = stem == "init" ? "tas" : "tas." + stem;
            var chunk = _script.LoadString(File.ReadAllText(file), null, modName);
            modules.Set(modName, chunk);
        }
        _script.Globals["__modules"] = modules;
    }

    /// <summary>Resume the script for exactly one frame of output.</summary>
    public void Advance()
    {
        if (Finished) return;

        foreach (var b in ButtonNames) _prev[b] = _cur[b];

        DynValue frame = _co.Coroutine.Resume();

        if (_co.Coroutine.State == CoroutineState.Dead)
        {
            Finished = true;
            foreach (var a in AxisNames) _axis[a] = 0f;   // don't let the last
            foreach (var b in ButtonNames) _cur[b] = false; // frame's input leak
            return;
        }

        if (frame.Type != DataType.Table)
            throw new ScriptRuntimeException("live script yielded a non-frame value");

        var t = frame.Table;
        foreach (var a in AxisNames)
            _axis[a] = (float)(t.Get(a).CastToNumber() ?? 0.0);
        foreach (var b in ButtonNames)
            _cur[b] = t.Get(b).CastToBool();

        var s = t.Get("Speed");
        Speed = s.IsNil() ? (float?)null : (float)s.Number;
        Reset = t.Get("Reset Checkpoint").CastToBool();

        FrameCount++;
    }

    public float GetAxis(string name) => _axis.TryGetValue(name, out var v) ? v : 0f;
    public bool GetButton(string name) => _cur.TryGetValue(name, out var v) && v;
    public bool GetButtonDown(string name) => GetButton(name) && !(_prev.TryGetValue(name, out var p) && p);
    public bool GetButtonUp(string name) => !GetButton(name) && (_prev.TryGetValue(name, out var p) && p);
}
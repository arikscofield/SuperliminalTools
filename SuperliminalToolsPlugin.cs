using BepInEx;
using HarmonyLib;
using SuperliminalTools.Patches;
using SuperliminalTools.PracticeMod;
using SuperliminalTools.TASMod;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using BepInEx.Logging;

#if LEGACY
using BepInEx.Unity.IL2CPP;
#endif

namespace SuperliminalTools;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
#if LEGACY
public partial class SuperliminalToolsPlugin : BasePlugin
{
    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        Initialize();
    }
}
#else
public partial class SuperliminalToolsPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private void Awake()
    {
        Log = Logger;
        Initialize();
    }
}
#endif

public partial class SuperliminalToolsPlugin
{
    private void Initialize()
    {
        var args = Environment.GetCommandLineArgs();
        bool tasMode = args.Contains("--tas");

        var targetVersion = MyPluginInfo.PLUGIN_GUID.Replace(".superliminaltools", "");

        if(Application.version.IndexOf(targetVersion) < 0)
        {
            Log.LogError($"Plugin {MyPluginInfo.PLUGIN_GUID} targets {targetVersion} but game version is {Application.version}.");
            return;
        }

        Log.LogInfo($"{MyPluginInfo.PLUGIN_GUID} loaded! Mode: {(tasMode ? "TAS" : "Practice")}");

        // Create a persistent GameObject that survives scene transitions
        var go = new GameObject("SuperliminalTools");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;

#if LEGACY
        SuperliminalTools.Components.Utility.RegisterAllIL2CPPTypes();
#endif
        SuperliminalTools.Components.Utility.AddAllSharedComponents(go);
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        if (tasMode)
        {
            // --no-cpu-determinism disables the CPU determinism patch for A/B
            // timing and for confirming it is what keeps two machines in sync.
            if (args.Contains("--no-cpu-determinism"))
                Log.LogWarning("--no-cpu-determinism: CPU determinism patch DISABLED. " +
                               "Demos may desync across CPU vendors.");
            else
                InstallDeterminism();

            go.AddComponent<TASModController>();
            UnityEngineTimePatcher.Patch(Process.GetCurrentProcess());
            harmony.PatchAll();
        }
        else
        {
            go.AddComponent<PracticeModController>();
            harmony.PatchAll(typeof(NormalLoadingScreensPatch));
            harmony.PatchAll(typeof(DisableAlarmSoundPatch));
            harmony.PatchAll(typeof(DontPauseOnLostFocusPatch));
#if HAS_WARNING_CONTROLLER
            harmony.PatchAll(typeof(DisableWarningScreenPatch));
#endif
#if LEGACY
            harmony.PatchAll(typeof(LegacyResetCheckpointPatch));
            harmony.PatchAll(typeof(HotCoffeeErrorPatch));
#endif
        }
    }
    
    
    // --- Cross-CPU determinism -------------------------------------------------
    //
    // Intel and AMD produce different results for the approximate reciprocal
    // instructions (RCPPS/RSQRTPS and friends), which PhysX uses in its contact
    // solver. Left alone, a demo recorded on one vendor desyncs on the other.
    // SuperliminalDeterminism.dll replaces every such site in UnityPlayer.dll
    // with exact IEEE arithmetic. The site list is generated per game version
    // and embedded as a resource, so there are no loose files to keep in sync.

    private const string NativeDll = "SuperliminalDeterminism.dll";
    private const string SiteResource = "rsqrt_sites.txt";

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibraryA(string path);

    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int InstallDeterminismPatches(uint[] rvas, int count);

#if DEBUG_PROBES
    [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi, EntryPoint = "DumpDeterminismHits")]
    private static extern void DumpDeterminismHitsNative(string outFile);
#endif

    /// <summary>Game install root (the folder containing Superliminal.exe).</summary>
    internal static string GameRoot => Path.GetDirectoryName(Application.dataPath);

    /// <summary>Folder this plugin was loaded from; the native DLL sits beside it.</summary>
    private static string PluginDir
    {
        get
        {
            try
            {
                var loc = typeof(SuperliminalToolsPlugin).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch { /* IL2CPP may not report a location */ }
            return GameRoot;
        }
    }

    internal static bool DeterminismActive { get; private set; }

    private static void InstallDeterminism()
    {
        var dll = Path.Combine(PluginDir, NativeDll);
        if (!File.Exists(dll))
        {
            Log.LogError($"{NativeDll} not found next to the plugin. " +
                         "Physics will NOT be deterministic across CPU vendors.");
            return;
        }
        if (LoadLibraryA(dll) == IntPtr.Zero)
        {
            Log.LogError($"Failed to load {NativeDll} (Win32 error {Marshal.GetLastWin32Error()}). " +
                         "Physics will NOT be deterministic across CPU vendors.");
            return;
        }

        var rvas = LoadSiteList();
        if (rvas == null || rvas.Length == 0)
        {
            Log.LogError("Embedded determinism site list is missing or empty. " +
                         "Physics will NOT be deterministic across CPU vendors.");
            return;
        }

        int patched = InstallDeterminismPatches(rvas, rvas.Length);
        if (patched < 0)
        {
            Log.LogError($"Determinism patch failed (code {patched}). " +
                         "Physics will NOT be deterministic across CPU vendors.");
            return;
        }

        DeterminismActive = patched > 0;

        if (patched == rvas.Length)
            Log.LogInfo($"CPU Determinism: patched all {patched} sites.");
        else
            Log.LogWarning($"CPU Determinism: patched {patched} of {rvas.Length} sites. " +
                           "Demos may desync across CPU vendors.");
    }

    /// <summary>Reads the version-specific RVA list embedded at build time.</summary>
    private static uint[] LoadSiteList()
    {
        using (var stream = typeof(SuperliminalToolsPlugin).Assembly
                   .GetManifestResourceStream(SiteResource))
        {
            if (stream == null) return null;
            using (var reader = new StreamReader(stream))
            {
                var list = new List<uint>(1024);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (uint.TryParse(line, NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out var rva))
                        list.Add(rva);
                }
                return list.ToArray();
            }
        }
    }

#if DEBUG_PROBES
    /// <summary>Writes per-site trap counts to hits.txt in the game root.</summary>
    internal static void DumpDeterminismHits()
    {
        if (!DeterminismActive)
        {
            Log.LogWarning("Determinism patch not active; nothing to dump.");
            return;
        }
        var path = Path.Combine(GameRoot, "hits.txt");
        DumpDeterminismHitsNative(path);
        Log.LogInfo($"Determinism hits written to {path}");
    }
#endif
}
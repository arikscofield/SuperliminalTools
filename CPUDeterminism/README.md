# Cross-CPU determinism

Intel and AMD return different results for the approximate reciprocal instructions (`RCPPS`, `RSQRTPS` and friends). x86 only specifies them to ~12 bits and leaves the rest to the microarchitecture. PhysX uses them in its rigidbody contact solver.

`SuperliminalDeterminism.dll` overwrites each of those instructions in `UnityPlayer.dll` with `UD2`. An exception handler catches the trap, recomputes the value with plain IEEE divide and sqrt (identical on every x86 CPU), and resumes. Installed in TAS mode only.

Two consequences worth knowing: demos recorded *before* this existed may not play the same under it, and it costs ~1,500–2,000 traps per frame.

The BepInEx log should say `CPU Determinism: patched all N sites`. Anything else is logged as an error or warning. Launch with `--no-cpu-determinism` to turn it off.

## Adding a new game version

```
pip install capstone pefile
python generate_sites.py "C:/Games/Superliminal/<version>/UnityPlayer.dll"
```

Writes `Sites/rsqrt_sites_<version>.txt` (version label comes from the DLL's parent folder, or `--version`). The csproj embeds the matching file automatically, so a rebuild picks it up; add the version to `BuildAllVersions` too.

| version | sites |
|---|---|
| 1.0.2019.11.12 | 861 |
| 1.10.2020.7.6 | 924 |
| 1.10.2020.12.10 | 924 |
| 1.10.2023.2.17 | 907 |

## Rebuilding the DLL

Committed prebuilt, so you only need this if you change `determinism.cpp`:

```
build.cmd
```

Run it from an **x64 Native Tools Command Prompt for VS**. MinGW or clang work too:

```
g++     -shared -O2 -o SuperliminalDeterminism.dll determinism.cpp -static-libgcc -static-libstdc++
clang++ -shared -O2 -o SuperliminalDeterminism.dll determinism.cpp
```

Must be x64; `Win32 error 193` in the log means you built 32-bit.

## Verifying / measuring

Build the `DebugProbes` configuration for two hotkeys: **F9** runs an isolated 300-step rigidbody probe (files should be byte-identical across machines, but ignore any run whose header says `selfcheck : FAIL`), and **F8** dumps trap counts to `hits.txt`.

F9 also reports `# sim ms`, so running it with and without `--no-cpu-determinism` gives you the patch's cost on a fixed workload. 

If the cost ever matters, the traps can be replaced with jump trampolines that do the same exact arithmetic inline. The obstacle is that the sites are 3–4 bytes and a `jmp rel32` needs 5, so neighbouring instructions have to be relocated.

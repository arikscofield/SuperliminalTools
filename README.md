# SuperliminalTools-lua

A fork of [SuperliminalTools](https://github.com/Micrologist/SuperliminalTools) that adds Lua for TAS scripting.

A BepInEx plugin for Superliminal for speedrun practice and tool assisted speedrunning.

## Installation

Download the [release](https://github.com/arikscofield/SuperliminalTools/releases/latest) matching your game version and extract the contents into your game folder.

Use the included `.bat` files to launch in practice mod, TAS mod, or with no mods. Launched manually, the plugin loads practice mod; pass `--tas` for TAS mod.

This mod also includes things for mitigating differences between cpu vendors. If you want to go without this, pass `--no-cpu-determinism` when launching the game.
You can read more about this here [`CPUDeterminism/README.md`](CPUDeterminism/README.md).

## Usage

Create a `.lua` file inside `/demos`, starting with `local tas = require("tas").new()`. See `/Lua/examples` for examples and `/Lua/tas/commands.lua` for available commands. `.csv` demos also work.

You can split a file into sections using `local s = require("tas.sections").new(tas, 0)`, where you can replace 0 with the checkpoint index you want to start from (for faster creation)
Sections are defined as follows:

```lua
s.section("Section Name", CHECKPOINT_INDEX, function()
    tas.forward(1)
    ...
end)
```

F11 to open a .lua demo. Saving the file will automatically detect and replay the demo.

You can use `-` and `=` to live-control playback speed

## Building

Place the referenced game libraries into `GameLibs/$GameVersion/` — for modern versions from `Superliminal_Data/Managed/`, for legacy (IL2CPP) versions the BepInEx-generated ones from `BepInEx/interop/`.

`Release` also writes release zips to `Releases/`. The `DebugProbes` configuration adds F8 (dump determinism trap counts) and F9 (physics determinism probe), plus per-frame `DesyncLog` output during playback; release builds have neither.

`SuperliminalDeterminism.dll` is committed prebuilt, so a C++ toolchain is only needed to change it.

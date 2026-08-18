# Presetway

## Foreword

This project in its entirety is coded by AI. It is "vibe coded." The premise, as well as ideas for features and iterations are the only human elements. All code for this project (not its dependencies) is AI generated.

I do not have the technical expertise to properly and thoroughly review this project's code or permanently maintain this project.

I wanted something like this to exist for a long time, and now it does.

If you are someone who is not okay with the use of AI in coding/creating things, then this project may not be for you. I apologize.

If you are someone who actually knows what they're doing, and ideally a trusted plugin and/or addon developer already and would like to take over this project, make changes to it, review it, etc, please [open an issue](https://github.com/Bastaklis1/Presetway/issues) or reach out directly.

Upstream licenses apply wherever their code is used (see Credits below). Anything else in this repo should be considered MIT -- see `LICENSE`. 

---

Automatically switches ReShade presets based on FFXIV zone, weather, and time
of day. Two parts working together:

- **A Dalamud plugin** that reads your current zone/weather/Eorzea time,
  resolves it against rules you define, and publishes the result.
- **A ReShade addon** that receives that and actually switches the preset.

Optional third piece: if you use [Weatherman](https://github.com/NightmareXIV/Weatherman)
to force specific weather/time per zone, Presetway can read its override
state too, so the preset you get matches what Weatherman is actually showing
rather than the real (unmodified) game state underneath it. See the
"Weatherman integration" section below -- this needs a small addition to
Weatherman that is offered in a fork linked below.

## Installing (not building from source)

### Presetway (Dalamud plugin)

1. In-game or via the Dalamud console, open `/xlsettings`.
2. **Experimental** tab -> **Custom Plugin Repositories** -> add:
   ```
   https://raw.githubusercontent.com/Bastaklis1/Presetway/main/pluginmaster.json
   ```
3. `/xlplugins` -> find Presetway -> install.
4. `/presetway` opens the window. First thing to do: add a rule or two,
   pointing at real preset files on your machine.

### Presetway (ReShade addon) -- separate, manual step, always

Dalamud has no way to install this part -- ReShade addons aren't Dalamud
plugins, there's no equivalent repository mechanism, and there isn't going to
be one. This part is always: download, rename if needed, drop in a folder.

1. Requires ReShade's **"full add-on support" (unsigned)** build. If REST or
   any other ReShade addon already works for you, you already have this.
2. Download `Presetway.addon` from the
   [latest release](https://github.com/Bastaklis1/Presetway/releases/latest).
3. Place it in the same folder as your other ReShade addons -- next to
   `ffxiv_dx11.exe`.
4. Launch the game. Check ReShade's log (Home tab -> Log) for
   `Presetway addon: Sharingway subscriber ready.` to confirm it loaded.

### Weatherman integration (optional)

Presetway can detect and follow Weatherman's active overrides, but this needs
two methods (`GetDisplayedWeather`/`GetDisplayedTime`) exposed on Weatherman's
IPC surface that do not exist in the upstream release. Until/unless that's
ever considered:

- Use [this fork](https://github.com/Bastaklis1/Weatherman/tree/Presetway_Compatibility) instead
  of upstream Weatherman, **or**
- Just leave "Check for Weatherman overrides" unchecked in Presetway's
  settings (it's off by default) -- everything else works fine without it,
  reading real game state directly.

## Building from source

See `Presetway-Dalamud/README.md` and `Presetway-ReShade-Addon/` build notes
for the dev setup (Visual Studio 2026, .NET desktop + C++ desktop workloads,
CMake). Short version: open `Presetway-Dalamud/Presetway.sln` for the plugin,
open the `Presetway-ReShade-Addon/` folder directly in Visual Studio (CMake
project, not a `.sln`) for the addon.

## Credits / third-party code

Presetway vendors source directly from these projects rather than requiring
separate downloads:

- [Sharingway](https://github.com/gposingway/sharingway) (MIT) -- the
  cross-language IPC layer connecting the Dalamud plugin and the ReShade
  addon. `Presetway-Dalamud/Presetway/Vendor/Sharingway/` and
  `Presetway-ReShade-Addon/ThirdParty/Sharingway/`.
- [ReShade](https://github.com/crosire/reshade) (BSD-3-Clause OR MIT) -- the
  addon SDK headers. `Presetway-ReShade-Addon/ThirdParty/reshade-sdk/`.
- [Weatherman](https://github.com/NightmareXIV/Weatherman) (AGPL-3.0) --
  integration via its public IPC surface only; no Weatherman code is vendored
  into this repo.

## Status

Working prototype, tested by hand across multiple sessions, not yet reviewed
or tested by anyone besides the author. 

Probably as complete as it's going to get. 

Likely to become abandoned or unmaintained.

Thought the crash when preset swapping was fixed with the new .addon. It was handling large preset swaps fine, now it's handling no preset swaps without crashing - same as the first iteration. 

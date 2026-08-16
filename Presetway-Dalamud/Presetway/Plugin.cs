using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Presetway.Windows;
using Sharingway.Net;

namespace Presetway;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/presetway";
    private const string SharingwayProviderName = "Presetway";

    // TimeOfDay needs the string converter, or a rules file with "timeOfDay": "Night"
    // fails to parse -- System.Text.Json defaults to numeric enum values otherwise.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Configuration Configuration { get; }

    internal GameStateWatcher Watcher { get; }
    internal RuleEngine RuleEngine { get; } = new();
    internal List<PresetRule> RulesEditable { get; private set; } = new();

    /// <summary>(id, display name) pairs for every named zone, sorted alphabetically. Empty if the sheet read failed -- UI should fall back to manual numeric entry.</summary>
    internal List<(uint Id, string Name)> ZoneList { get; } = new();

    /// <summary>(id, display name) pairs for every weather type, sorted alphabetically.</summary>
    internal List<(byte Id, string Name)> WeatherList { get; } = new();

    private readonly WindowSystem windowSystem = new("Presetway");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;

    private Provider? sharingwayProvider;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        TryAutoDetectReShadeIniPath();

        PopulateGameData();
        LoadRules();
        InitializeSharingway();

        Watcher = new GameStateWatcher(ClientState, Framework, Log, Configuration);
        Watcher.StateChanged += OnStateChanged;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;

        // Publish once immediately so the ReShade side has something to react to
        // as soon as both pieces are up, rather than waiting for the next change.
        PublishEffectiveState();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Presetway: no args = open window, 'status' = text status, 'reload' = re-read rules file, 'config' = open settings.",
        });

        Log.Information("Presetway loaded.");
    }

    private void PopulateGameData()
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    string? name = null;
                    try { name = row.PlaceName.ValueNullable?.Name.ToString(); }
                    catch { /* some rows (instances, cutscene maps) don't resolve a place name; skip them */ }

                    if (!string.IsNullOrWhiteSpace(name))
                        ZoneList.Add((row.RowId, name!));
                }
                ZoneList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Presetway: couldn't read the TerritoryType sheet; zone dropdown will be empty (use manual ID entry).");
        }

        try
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    var name = row.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        WeatherList.Add(((byte)row.RowId, name));
                }
                WeatherList.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Presetway: couldn't read the Weather sheet; weather dropdown will be empty (use manual ID entry).");
        }
    }

    internal string GetZoneName(uint id) => ZoneList.FirstOrDefault(z => z.Id == id).Name is { Length: > 0 } n ? n : $"#{id}";
    internal string GetWeatherName(byte id) => WeatherList.FirstOrDefault(w => w.Id == id).Name is { Length: > 0 } n ? n : $"#{id}";

    /// <summary>
    /// Reads ReShade's own .ini for its "PresetPath=" line -- this is how ReShade
    /// remembers which preset was last active, so it's a reasonable stand-in for
    /// "whatever preset is currently enabled" without needing anything from the
    /// (not yet built/tested) addon side. Resolves relative paths against the
    /// ini's own folder, same as ReShade does internally.
    /// </summary>
    /// <summary>
    /// Strips whitespace and, importantly, surrounding quote characters --
    /// Windows Explorer's "Copy as path" wraps paths in "..." and pasting that
    /// straight into a text box otherwise leaves the quotes as literal characters
    /// in the string, which then fails every path check silently.
    /// </summary>
    internal static string CleanPathInput(string input) => input.Trim().Trim('"');

    /// <summary>
    /// If ReShadeIniPath isn't set yet, guesses it from wherever ffxiv_dx11.exe
    /// (this very process) is actually running from, since ReShade.ini lives
    /// right next to it for essentially every install. Only applied if a file
    /// is actually found there -- never overwrites a value you already set.
    /// </summary>
    private void TryAutoDetectReShadeIniPath()
    {
        if (!string.IsNullOrWhiteSpace(Configuration.ReShadeIniPath))
            return;

        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return;

            var candidate = Path.Combine(Path.GetDirectoryName(exePath)!, "ReShade.ini");
            if (File.Exists(candidate))
            {
                Configuration.ReShadeIniPath = candidate;
                Configuration.Save();
                Log.Information($"Presetway: auto-detected ReShade.ini at '{candidate}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Presetway: ReShade.ini auto-detection failed (non-fatal, settings can still be set by hand).");
        }
    }

    /// <summary>
    /// Reads ReShade's own .ini for its "PresetPath=" line -- this is how ReShade
    /// remembers which preset was last active, so it's a reasonable stand-in for
    /// "whatever preset is currently enabled" without needing anything from the
    /// addon side. Resolves relative paths against the ini's own folder, same as
    /// ReShade does internally.
    /// </summary>
    internal string? TryReadCurrentReShadePresetPath()
    {
        var iniPath = CleanPathInput(Configuration.ReShadeIniPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            Log.Warning($"Presetway: ReShade.ini not found at '{iniPath}'. Set the correct path in Presetway settings (/presetway config).");
            return null;
        }

        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("PresetPath", StringComparison.OrdinalIgnoreCase))
                    continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0)
                    continue;

                var value = trimmed[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(value))
                    continue;

                if (!Path.IsPathRooted(value))
                    value = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(iniPath)!, value));

                return value;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Presetway: failed to read ReShade.ini.");
        }

        Log.Warning("Presetway: no PresetPath= line found in ReShade.ini.");
        return null;
    }

    private void InitializeSharingway()
    {
        try
        {
            sharingwayProvider = new Provider(
                SharingwayProviderName,
                "FFXIV zone + weather + time-of-day state for ReShade preset switching",
                new List<string> { "zone", "weather", "time-of-day", "reshade-preset" });

            if (!sharingwayProvider.Initialize())
            {
                Log.Warning("Presetway: Sharingway provider failed to initialize. State will still be logged, but nothing will reach the ReShade addon.");
                sharingwayProvider = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Presetway: failed to set up the Sharingway provider.");
            sharingwayProvider = null;
        }
    }

    internal bool LoadRules()
    {
        try
        {
            var path = ResolveRulesPath();
            if (!File.Exists(path))
            {
                Log.Warning($"Presetway: no rules file at '{path}'. Add rules via the Presetway window, or copy presetway-rules.example.json there (renamed).");
                RulesEditable = new List<PresetRule>();
                RuleEngine.SetRules(RulesEditable);
                return true;
            }

            var json = File.ReadAllText(path);
            var rules = JsonSerializer.Deserialize<List<PresetRule>>(json, JsonOptions) ?? new List<PresetRule>();

            RulesEditable = rules;
            RuleEngine.SetRules(RulesEditable);
            Log.Information($"Presetway: loaded {rules.Count} rule(s) from '{path}'.");
            return true;
        }
        catch (Exception ex)
        {
            // This used to fail silently from the caller's point of view -- the
            // rules file could be empty/malformed (e.g. after hand-editing) and
            // "Reload from disk" would appear to just do nothing, since the old
            // in-memory rules were left untouched rather than cleared. Surface it.
            Log.Error(ex, "Presetway: failed to load the rules file.");
            ChatGui.Print($"[Presetway] Reload failed: {ex.Message}. Rules file may be malformed -- previous rules were kept as-is.");
            return false;
        }
    }

    internal void OpenSettings() => configWindow.IsOpen = true;

    internal void SaveRules()
    {
        try
        {
            var path = ResolveRulesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(RulesEditable, JsonOptions);
            File.WriteAllText(path, json);
            RuleEngine.SetRules(RulesEditable);
            Log.Information($"Presetway: saved {RulesEditable.Count} rule(s) to '{path}'.");
            PublishEffectiveState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Presetway: failed to save the rules file.");
        }
    }

    internal void AddRule(PresetRule rule)
    {
        RulesEditable.Add(rule);
        SaveRules();
    }

    internal void RemoveRule(PresetRule rule)
    {
        RulesEditable.Remove(rule);
        SaveRules();
    }

    private string ResolveRulesPath()
    {
        var configured = Configuration.RulesFilePath;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(PluginInterface.GetPluginConfigDirectory(), configured);
    }

    /// <summary>
    /// Awareness of Weatherman's override state, via the IPC it exposes (call-gate
    /// names confirmed against its actual source). As of your forked build adding
    /// GetDisplayedWeather()/GetDisplayedTime(), this now actually substitutes
    /// those values into rule resolution/publishing when an override is active --
    /// see GetEffectiveState() below, which is the single source of truth both
    /// PublishEffectiveState and the status UI read from. Falls back to real game
    /// state whenever Weatherman isn't loaded, the checkbox is off, no override is
    /// active for that dimension, or the IPC call fails for any reason (e.g. your
    /// fork uses a different method name/signature than this expects).
    /// </summary>
    internal bool? WeathermanWeatherOverrideActive { get; private set; }
    internal bool? WeathermanTimeOverrideActive { get; private set; }
    internal byte? WeathermanDisplayedWeatherId { get; private set; }
    internal TimeOfDay? WeathermanDisplayedTimeOfDay { get; private set; }

    /// <summary>
    /// Explicit existence check via InstalledPlugins, rather than relying purely
    /// on try/catch around the IPC calls -- avoids throwing (and paying .NET's
    /// real exception-handling cost) on every single state change for everyone
    /// who doesn't have Weatherman installed at all, and makes "is it actually
    /// there" a direct, visible check rather than an implicit side effect of
    /// error handling.
    /// </summary>
    private bool IsWeathermanLoaded() =>
        PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Weatherman" && p.IsLoaded);

    private void RefreshWeathermanStatus()
    {
        WeathermanDisplayedWeatherId = null;
        WeathermanDisplayedTimeOfDay = null;

        if (!Configuration.CheckWeathermanOverrides)
        {
            // Fully off: don't even check whether Weatherman is installed.
            WeathermanWeatherOverrideActive = null;
            WeathermanTimeOverrideActive = null;
            return;
        }

        if (!IsWeathermanLoaded())
        {
            WeathermanWeatherOverrideActive = null;
            WeathermanTimeOverrideActive = null;
            return;
        }

        try
        {
            var isWeatherCustom = PluginInterface.GetIpcSubscriber<bool>("Weatherman.IsWeatherCustom");
            WeathermanWeatherOverrideActive = isWeatherCustom.InvokeFunc();
        }
        catch (Exception ex)
        {
            // Installed and loaded, but the call still failed -- unlike "not
            // installed" this is actually unexpected, worth a log line.
            Log.Debug(ex, "Presetway: Weatherman is loaded but IsWeatherCustom IPC call failed.");
            WeathermanWeatherOverrideActive = null;
        }

        if (WeathermanWeatherOverrideActive == true)
        {
            try
            {
                var getWeather = PluginInterface.GetIpcSubscriber<byte>("Weatherman.GetDisplayedWeather");
                WeathermanDisplayedWeatherId = getWeather.InvokeFunc();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Presetway: Weatherman reports a weather override is active, but GetDisplayedWeather failed -- " +
                    "falling back to real weather for now. If your fork uses a different method name/signature, this needs to match it exactly.");
            }
        }

        try
        {
            var isTimeCustom = PluginInterface.GetIpcSubscriber<bool>("Weatherman.IsTimeCustom");
            WeathermanTimeOverrideActive = isTimeCustom.InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Presetway: Weatherman is loaded but IsTimeCustom IPC call failed.");
            WeathermanTimeOverrideActive = null;
        }

        if (WeathermanTimeOverrideActive == true)
        {
            try
            {
                var getTime = PluginInterface.GetIpcSubscriber<uint>("Weatherman.GetDisplayedTime");
                var eorzeaSeconds = getTime.InvokeFunc();
                var eorzeaHour = (eorzeaSeconds / 3600.0) % 24.0;
                WeathermanDisplayedTimeOfDay = Configuration.ResolveTimeOfDay(eorzeaHour);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Presetway: Weatherman reports a time override is active, but GetDisplayedTime failed -- " +
                    "falling back to real time for now. If your fork uses a different method name/signature, this needs to match it exactly.");
            }
        }
    }

    /// <summary>
    /// The single source of truth for "what should actually drive rule
    /// resolution and publishing right now" -- zone always comes from real game
    /// state (Weatherman never overrides which zone you're in), weather and time
    /// each independently prefer Weatherman's displayed value when that specific
    /// dimension has an active, successfully-read override, and fall back to real
    /// state otherwise. Used by PublishEffectiveState, the status command, and
    /// the main window so all three always agree.
    /// </summary>
    internal (uint TerritoryId, byte WeatherId, TimeOfDay TimeOfDay) GetEffectiveState() =>
        (Watcher.CurrentTerritoryId,
         WeathermanDisplayedWeatherId ?? Watcher.CurrentWeatherId,
         WeathermanDisplayedTimeOfDay ?? Watcher.CurrentTimeOfDay);

    private void OnStateChanged(uint territoryId, byte weatherId, TimeOfDay timeOfDay) => PublishEffectiveState();

    private void PublishEffectiveState()
    {
        RefreshWeathermanStatus();

        var (territoryId, weatherId, timeOfDay) = GetEffectiveState();
        var rule = RuleEngine.Resolve(territoryId, weatherId, timeOfDay);

        var payload = new
        {
            territoryId,
            weatherId,
            timeOfDay = timeOfDay.ToString(),
            presetPath = rule?.PresetPath,
            label = rule?.Label,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        if (sharingwayProvider is { IsOnline: true })
        {
            if (!sharingwayProvider.PublishData(payload))
                Log.Warning("Presetway: PublishData returned false.");
        }

        if (rule is null)
            Log.Debug($"Presetway: no matching rule for territory={territoryId}, weather={weatherId}, timeOfDay={timeOfDay}.");
        else
            Log.Information($"Presetway: territory={territoryId} weather={weatherId} timeOfDay={timeOfDay} -> preset '{rule.PresetPath}' ({rule.Label})");
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            if (LoadRules())
            {
                PublishEffectiveState();
                ChatGui.Print($"[Presetway] Rules reloaded — {RulesEditable.Count} rule(s) now loaded.");
            }
            return;
        }

        if (args.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.IsOpen = true;
            return;
        }

        if (args.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var (territoryId, weatherId, timeOfDay) = GetEffectiveState();
            var rule = RuleEngine.Resolve(territoryId, weatherId, timeOfDay);
            var bridgeState = sharingwayProvider is { IsOnline: true } ? "connected" : "not connected";
            ChatGui.Print(
                $"[Presetway] zone={GetZoneName(Watcher.CurrentTerritoryId)} ({Watcher.CurrentTerritoryId}) " +
                $"weather={GetWeatherName(weatherId)} ({weatherId}) " +
                $"time={timeOfDay} -> {(rule?.PresetPath ?? "(no matching rule)")} " +
                $"| bridge: {bridgeState} | {RulesEditable.Count} rule(s) loaded");

            if (WeathermanWeatherOverrideActive == true)
            {
                ChatGui.Print(WeathermanDisplayedWeatherId.HasValue
                    ? $"[Presetway] Weatherman weather override active -> {GetWeatherName(WeathermanDisplayedWeatherId.Value)} (used above)"
                    : "[Presetway] Weatherman weather override active, but its value couldn't be read -- falling back to real weather. Check /xllog.");
            }

            if (WeathermanTimeOverrideActive == true)
            {
                ChatGui.Print(WeathermanDisplayedTimeOfDay.HasValue
                    ? $"[Presetway] Weatherman time override active -> {WeathermanDisplayedTimeOfDay.Value} (used above)"
                    : "[Presetway] Weatherman time override active, but its value couldn't be read -- falling back to real time. Check /xllog.");
            }
            return;
        }

        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
        Watcher.StateChanged -= OnStateChanged;
        Watcher.Dispose();
        sharingwayProvider?.Dispose();
    }
}

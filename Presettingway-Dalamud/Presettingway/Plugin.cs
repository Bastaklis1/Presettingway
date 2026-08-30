using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Presettingway.Windows;
using Sharingway.Net;

namespace Presettingway;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pway";
    private const string SharingwayProviderName = "Presettingway";

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

    private readonly WindowSystem windowSystem = new("Presettingway");

    /// <summary>
    /// Dalamud's own built-in ImGui file/folder picker -- drawn as a normal
    /// ImGui window, same as everything else here, so no native interop or
    /// P/Invoke needed at all. Its .Draw() has to run every frame regardless
    /// of whether a dialog is currently open (it no-ops internally when not),
    /// so it's wired into the same Draw subscription as the window system.
    /// </summary>
    internal readonly FileDialogManager FileDialogManager = new();
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
        Framework.Update += OnFrameworkUpdateForWeathermanPolling;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += FileDialogManager.Draw;
        PluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;
        PluginInterface.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;

        // Publish once immediately so the ReShade side has something to react to
        // as soon as both pieces are up, rather than waiting for the next change.
        PublishEffectiveState();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Presettingway: no args = open window, 'status' = text status, 'reload' = re-read rules file, 'config' = open settings.",
        });

        Log.Information("Presettingway loaded.");
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
            Log.Warning(ex, "Presettingway: couldn't read the TerritoryType sheet; zone dropdown will be empty (use manual ID entry).");
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
            Log.Warning(ex, "Presettingway: couldn't read the Weather sheet; weather dropdown will be empty (use manual ID entry).");
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
                Log.Information($"Presettingway: auto-detected ReShade.ini at '{candidate}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Presettingway: ReShade.ini auto-detection failed (non-fatal, settings can still be set by hand).");
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
            Log.Warning($"Presettingway: ReShade.ini not found at '{iniPath}'. Set the correct path in Presettingway settings (/pway config).");
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
            Log.Warning(ex, "Presettingway: failed to read ReShade.ini.");
        }

        Log.Warning("Presettingway: no PresetPath= line found in ReShade.ini.");
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
                Log.Warning("Presettingway: Sharingway provider failed to initialize. State will still be logged, but nothing will reach the ReShade addon.");
                sharingwayProvider = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Presettingway: failed to set up the Sharingway provider.");
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
                Log.Warning($"Presettingway: no rules file at '{path}'. Add rules via the Presettingway window, or copy presettingway-rules.example.json there (renamed).");
                RulesEditable = new List<PresetRule>();
                RuleEngine.SetRules(RulesEditable);
                return true;
            }

            var json = File.ReadAllText(path);
            var rules = JsonSerializer.Deserialize<List<PresetRule>>(json, JsonOptions) ?? new List<PresetRule>();

            RulesEditable = rules;
            RuleEngine.SetRules(RulesEditable);
            Log.Information($"Presettingway: loaded {rules.Count} rule(s) from '{path}'.");
            return true;
        }
        catch (Exception ex)
        {
            // This used to fail silently from the caller's point of view -- the
            // rules file could be empty/malformed (e.g. after hand-editing) and
            // "Reload from disk" would appear to just do nothing, since the old
            // in-memory rules were left untouched rather than cleared. Surface it.
            Log.Error(ex, "Presettingway: failed to load the rules file.");
            ChatGui.Print($"[Presettingway] Reload failed: {ex.Message}. Rules file may be malformed -- previous rules were kept as-is.");
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
            Log.Information($"Presettingway: saved {RulesEditable.Count} rule(s) to '{path}'.");
            PublishEffectiveState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Presettingway: failed to save the rules file.");
        }
    }

    internal void AddRule(PresetRule rule)
    {
        RulesEditable.Add(rule);
        SaveRules();
        TrySavePresetCopy(rule);
    }

    /// <summary>
    /// The rules filename used inside every collection folder -- deliberately
    /// the same literal name regardless of what a given person's own
    /// personal RulesFilePath happens to be configured as, so every
    /// collection is self-describing and interchangeable with any other
    /// user's rather than depending on one person's local naming choice.
    /// </summary>
    private const string CollectionRulesFileName = "presettingway-rules.json";

    /// <summary>
    /// "&lt;PresetsFolder&gt;\Presettingway\", the root all collections live
    /// under. Null if no presets folder is configured -- collections have
    /// nowhere to go without one.
    /// </summary>
    internal string? GetPresettingwayFolder() =>
        string.IsNullOrWhiteSpace(Configuration.PresetsFolder)
            ? null
            : Path.Combine(Configuration.PresetsFolder, "Presettingway");

    /// <summary>
    /// "&lt;PresetsFolder&gt;\Presettingway\&lt;ActiveCollectionName&gt;\" --
    /// null if there's no presets folder, or no active collection name set.
    /// Doesn't check the folder actually exists on disk; callers that need
    /// to read/write rules create it as needed.
    /// </summary>
    internal string? GetActiveCollectionFolder()
    {
        var root = GetPresettingwayFolder();
        if (root is null || string.IsNullOrWhiteSpace(Configuration.ActiveCollectionName))
            return null;
        return Path.Combine(root, Configuration.ActiveCollectionName);
    }

    /// <summary>
    /// Every existing collection name, found by listing subfolders of
    /// "&lt;PresetsFolder&gt;\Presettingway\". Empty if there's no presets
    /// folder, or the Presettingway folder doesn't exist yet (nothing's ever
    /// been created).
    /// </summary>
    internal List<string> ListCollections()
    {
        var root = GetPresettingwayFolder();
        if (root is null || !Directory.Exists(root))
            return new List<string>();

        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>
    /// Creates a brand new, empty collection named <paramref name="name"/>
    /// under "&lt;PresetsFolder&gt;\Presettingway\", switches RulesMode to
    /// Collection, makes it the active one, and reloads rules (empty, since
    /// the collection is new) so the UI reflects the switch immediately.
    /// Fails if PresetsFolder isn't set, the name is blank/unsafe once
    /// sanitized, or a collection with that name already exists (use
    /// SwitchToCollection to activate an existing one instead).
    /// </summary>
    internal bool TryCreateCollection(string name)
    {
        var root = GetPresettingwayFolder();
        if (root is null)
        {
            Log.Warning("Presettingway: can't create a collection -- no presets folder is set in Settings.");
            return false;
        }

        var sanitized = SanitizeForFileName(name);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            Log.Warning("Presettingway: collection name is empty (or only contained characters that aren't valid in a folder name).");
            return false;
        }

        var folder = Path.Combine(root, sanitized);
        if (Directory.Exists(folder))
        {
            Log.Warning($"Presettingway: a collection named '{sanitized}' already exists -- switch to it instead of creating it again.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var emptyRulesJson = JsonSerializer.Serialize(new List<PresetRule>(), JsonOptions);
            File.WriteAllText(Path.Combine(folder, CollectionRulesFileName), emptyRulesJson);

            Configuration.RulesMode = RulesMode.Collection;
            Configuration.ActiveCollectionName = sanitized;
            Configuration.Save();
            LoadRules();

            Log.Information($"Presettingway: created and switched to collection '{sanitized}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Presettingway: failed to create collection '{sanitized}'.");
            return false;
        }
    }

    /// <summary>
    /// Switches to an existing collection by name (must already exist under
    /// PresetsFolder\Presettingway\ -- use TryCreateCollection for a new
    /// one) and reloads rules so the UI reflects it immediately.
    /// </summary>
    internal bool SwitchToCollection(string name)
    {
        var root = GetPresettingwayFolder();
        if (root is null || !Directory.Exists(Path.Combine(root, name)))
        {
            Log.Warning($"Presettingway: collection '{name}' doesn't exist -- can't switch to it.");
            return false;
        }

        Configuration.RulesMode = RulesMode.Collection;
        Configuration.ActiveCollectionName = name;
        Configuration.Save();
        LoadRules();
        return true;
    }

    /// <summary>
    /// Switches back to the personal Local rules file. The collection itself
    /// (folder, rules file, tagged copies) is left completely untouched on
    /// disk -- this only changes which one Presettingway currently reads
    /// from and writes to.
    /// </summary>
    internal void SwitchToLocalMode()
    {
        Configuration.RulesMode = RulesMode.Local;
        Configuration.Save();
        LoadRules();
    }

    /// <summary>
    /// In Collection mode, saves a copy of the rule's preset into the active
    /// collection folder, named "{zoneId|Any}_{weatherId|Any}_{timeOfDay|Any}
    /// [_Label].ini". A no-op entirely in Local mode -- tagged copies only
    /// ever make sense as part of a shareable collection. Every rule that
    /// reaches this point already has one concrete zone/weather/time
    /// combination -- multi-select weather/time-of-day selections were
    /// already expanded into separate individual rules before AddRule was
    /// ever called (see MainWindow.AddRuleFromForm's cross-product loop) --
    /// so there's no AND/OR ambiguity to encode here at all. Never
    /// overwrites silently: any existing file at the target name gets moved
    /// into that collection's own "Old" subfolder (timestamped) first.
    /// </summary>
    private void TrySavePresetCopy(PresetRule rule)
    {
        if (Configuration.RulesMode != RulesMode.Collection)
            return;

        var collectionFolder = GetActiveCollectionFolder();
        if (collectionFolder is null)
        {
            Log.Warning("Presettingway: Collection mode is on, but no collection is active yet -- use \"Make New Collection\" first. Skipping tagged copy.");
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.PresetPath) || !File.Exists(rule.PresetPath))
        {
            Log.Warning($"Presettingway: couldn't save a tagged copy -- source preset '{rule.PresetPath}' doesn't exist.");
            return;
        }

        try
        {
            Directory.CreateDirectory(collectionFolder);

            var fileName = BuildTaggedPresetFileName(rule);
            var targetPath = Path.Combine(collectionFolder, fileName);

            // The collection folder is often also where the source preset
            // already lives once a collection's been in use for a while --
            // guard against source and target resolving to the literal same
            // file (File.Copy onto itself throws rather than being a
            // harmless no-op).
            if (string.Equals(Path.GetFullPath(rule.PresetPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("Presettingway: tagged copy target is the same file as the source preset -- nothing to do.");
                return;
            }

            if (File.Exists(targetPath))
            {
                var oldFolder = Path.Combine(collectionFolder, "Old");
                Directory.CreateDirectory(oldFolder);
                var archivedName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(fileName)}";
                File.Move(targetPath, Path.Combine(oldFolder, archivedName), overwrite: true);
                Log.Information($"Presettingway: archived the previous '{fileName}' to Old/ before overwriting.");
            }

            File.Copy(rule.PresetPath, targetPath, overwrite: true);
            Log.Information($"Presettingway: saved a tagged copy to '{targetPath}'.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Presettingway: failed to save a tagged preset copy. The rule itself was saved fine either way.");
        }
    }

    private static string BuildTaggedPresetFileName(PresetRule rule)
    {
        var zonePart = rule.TerritoryId.HasValue ? rule.TerritoryId.Value.ToString() : "Any";
        var weatherPart = rule.WeatherId.HasValue ? rule.WeatherId.Value.ToString() : "Any";
        var timePart = rule.TimeOfDayFilter.HasValue ? rule.TimeOfDayFilter.Value.ToString() : "Any";

        var baseName = $"{zonePart}_{weatherPart}_{timePart}";

        if (!string.IsNullOrWhiteSpace(rule.Label))
        {
            var sanitizedTag = SanitizeForFileName(rule.Label);
            if (!string.IsNullOrEmpty(sanitizedTag))
                baseName += $"_{sanitizedTag}";
        }

        return baseName + ".ini";
    }

    private static string SanitizeForFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }

    internal void RemoveRule(PresetRule rule)
    {
        RulesEditable.Remove(rule);
        SaveRules();
    }

    /// <summary>
    /// In Collection mode with an active collection, that collection's own
    /// rules file -- which is what makes a whole collection shareable as a
    /// single self-contained folder: drop it in, point Presets Folder + the
    /// active collection at it, get exactly that rule set. Otherwise (Local
    /// mode, or Collection mode with nothing active yet -- e.g. right after
    /// PresetsFolder gets cleared out from under an active collection) falls
    /// back to the personal %appdata% file, so there's always somewhere
    /// valid to read/write rather than throwing. LoadRules/SaveRules both
    /// already go through this one method, so it's the single place both
    /// reading and writing need to agree on.
    /// </summary>
    internal string ResolveRulesPath()
    {
        if (Configuration.RulesMode == RulesMode.Collection)
        {
            var collectionFolder = GetActiveCollectionFolder();
            if (collectionFolder != null)
                return Path.Combine(collectionFolder, CollectionRulesFileName);
        }

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
    /// Accepted Dalamud internal names for a Weatherman-compatible plugin, in
    /// priority order. "Weatherman" is the official upstream plugin -- once
    /// NightmareXIV publishes a release build that actually includes
    /// GetDisplayedWeather/GetDisplayedTime (merged to main, not yet shipped
    /// as of this writing), this is the only one that matters and the fork
    /// dependency goes away entirely. "Weatherman-Temp" is the stopgap fork,
    /// deliberately published under a different InternalName because Dalamud
    /// won't list a third-party repo's plugin under a name that collides with
    /// one already known from the official repo.
    ///
    /// ECommons' EzIPC derives its channel prefix from the loaded plugin's own
    /// InternalName when none is explicitly specified (confirmed directly in
    /// EzIPC.cs: `prefix ??= Svc.PluginInterface.InternalName`), and neither
    /// Weatherman's IPCProvider nor the fork's ever specifies one. So the
    /// fork's real IPC channels are "Weatherman-Temp.IsWeatherCustom" etc, not
    /// "Weatherman.IsWeatherCustom" -- the prefix has to be resolved from
    /// whichever one is actually loaded, not assumed.
    /// </summary>
    private static readonly string[] WeathermanInternalNames = ["Weatherman", "Weatherman-Temp"];

    /// <summary>
    /// The InternalName that was actually found loaded, used as the IPC
    /// channel prefix. Null when neither variant is loaded.
    /// </summary>
    private string? weathermanInternalName;

    /// <summary>
    /// Explicit existence check via InstalledPlugins, rather than relying purely
    /// on try/catch around the IPC calls -- avoids throwing (and paying .NET's
    /// real exception-handling cost) on every single state change for everyone
    /// who doesn't have Weatherman installed at all, and makes "is it actually
    /// there" a direct, visible check rather than an implicit side effect of
    /// error handling.
    /// </summary>
    private bool IsWeathermanLoaded()
    {
        weathermanInternalName = PluginInterface.InstalledPlugins
            .FirstOrDefault(p => WeathermanInternalNames.Contains(p.InternalName) && p.IsLoaded)
            ?.InternalName;
        return weathermanInternalName != null;
    }

    /// <summary>
    /// Presettingway previously only rechecked Weatherman's state when its own
    /// real-state-change event fired (a real zone/weather/time change) -- if
    /// Weatherman got toggled on/off/paused without any real state actually
    /// changing, nothing told Presettingway to look again, so it kept
    /// publishing stale data until the next real change. This polls
    /// Weatherman's IPC independently, on the same ~1s cadence as real state
    /// polling, and republishes immediately if anything Weatherman-related
    /// actually changed -- regardless of real game state. Zero overhead when
    /// the Weatherman checkbox is off (RefreshWeathermanStatus returns
    /// immediately without any IPC call in that case).
    /// </summary>
    private long lastWeathermanPollTicks = Environment.TickCount64;
    private const double WeathermanPollIntervalSeconds = 1.0;

    private void OnFrameworkUpdateForWeathermanPolling(IFramework fw)
    {
        var now = Environment.TickCount64;
        if ((now - lastWeathermanPollTicks) / 1000.0 < WeathermanPollIntervalSeconds)
            return;
        lastWeathermanPollTicks = now;

        if (!Configuration.CheckWeathermanOverrides)
            return;

        var previousWeatherActive = WeathermanWeatherOverrideActive;
        var previousWeatherId = WeathermanDisplayedWeatherId;
        var previousTimeActive = WeathermanTimeOverrideActive;
        var previousTimeOfDay = WeathermanDisplayedTimeOfDay;

        RefreshWeathermanStatus();

        var changed = previousWeatherActive != WeathermanWeatherOverrideActive
            || previousWeatherId != WeathermanDisplayedWeatherId
            || previousTimeActive != WeathermanTimeOverrideActive
            || previousTimeOfDay != WeathermanDisplayedTimeOfDay;

        if (changed)
        {
            Log.Debug("Presettingway: Weatherman state changed independent of any zone/weather/time change; republishing.");
            PublishEffectiveState();
        }
    }

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

        // Guaranteed non-null: IsWeathermanLoaded() just returned true.
        var prefix = weathermanInternalName!;

        try
        {
            var isWeatherCustom = PluginInterface.GetIpcSubscriber<bool>($"{prefix}.IsWeatherCustom");
            WeathermanWeatherOverrideActive = isWeatherCustom.InvokeFunc();
        }
        catch (Exception ex)
        {
            // Installed and loaded, but the call still failed -- unlike "not
            // installed" this is actually unexpected, worth a log line.
            Log.Debug(ex, $"Presettingway: {prefix} is loaded but IsWeatherCustom IPC call failed.");
            WeathermanWeatherOverrideActive = null;
        }

        if (WeathermanWeatherOverrideActive == true)
        {
            try
            {
                var getWeather = PluginInterface.GetIpcSubscriber<byte>($"{prefix}.GetDisplayedWeather");
                WeathermanDisplayedWeatherId = getWeather.InvokeFunc();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Presettingway: {prefix} reports a weather override is active, but GetDisplayedWeather failed -- " +
                    "falling back to real weather for now.");
            }
        }

        try
        {
            var isTimeCustom = PluginInterface.GetIpcSubscriber<bool>($"{prefix}.IsTimeCustom");
            WeathermanTimeOverrideActive = isTimeCustom.InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"Presettingway: {prefix} is loaded but IsTimeCustom IPC call failed.");
            WeathermanTimeOverrideActive = null;
        }

        if (WeathermanTimeOverrideActive == true)
        {
            try
            {
                var getTime = PluginInterface.GetIpcSubscriber<uint>($"{prefix}.GetDisplayedTime");
                var eorzeaSeconds = getTime.InvokeFunc();
                var eorzeaHour = (eorzeaSeconds / 3600.0) % 24.0;
                WeathermanDisplayedTimeOfDay = Configuration.ResolveTimeOfDay(eorzeaHour);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Presettingway: {prefix} reports a time override is active, but GetDisplayedTime failed -- " +
                    "falling back to real time for now.");
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
                Log.Warning("Presettingway: PublishData returned false.");
        }

        if (rule is null)
            Log.Debug($"Presettingway: no matching rule for territory={territoryId}, weather={weatherId}, timeOfDay={timeOfDay}.");
        else
            Log.Information($"Presettingway: territory={territoryId} weather={weatherId} timeOfDay={timeOfDay} -> preset '{rule.PresetPath}' ({rule.Label})");
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            if (LoadRules())
            {
                PublishEffectiveState();
                ChatGui.Print($"[Presettingway] Rules reloaded — {RulesEditable.Count} rule(s) now loaded.");
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
                $"[Presettingway] zone={GetZoneName(Watcher.CurrentTerritoryId)} ({Watcher.CurrentTerritoryId}) " +
                $"weather={GetWeatherName(weatherId)} ({weatherId}) " +
                $"time={timeOfDay} -> {(rule?.PresetPath ?? "(no matching rule)")} " +
                $"| bridge: {bridgeState} | {RulesEditable.Count} rule(s) loaded");

            if (WeathermanWeatherOverrideActive == true)
            {
                ChatGui.Print(WeathermanDisplayedWeatherId.HasValue
                    ? $"[Presettingway] Weatherman weather override active -> {GetWeatherName(WeathermanDisplayedWeatherId.Value)} (used above)"
                    : "[Presettingway] Weatherman weather override active, but its value couldn't be read -- falling back to real weather. Check /xllog.");
            }

            if (WeathermanTimeOverrideActive == true)
            {
                ChatGui.Print(WeathermanDisplayedTimeOfDay.HasValue
                    ? $"[Presettingway] Weatherman time override active -> {WeathermanDisplayedTimeOfDay.Value} (used above)"
                    : "[Presettingway] Weatherman time override active, but its value couldn't be read -- falling back to real time. Check /xllog.");
            }
            return;
        }

        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= FileDialogManager.Draw;
        windowSystem.RemoveAllWindows();
        Watcher.StateChanged -= OnStateChanged;
        Framework.Update -= OnFrameworkUpdateForWeathermanPolling;
        Watcher.Dispose();
        sharingwayProvider?.Dispose();
    }
}

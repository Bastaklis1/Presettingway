using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Presettingway.Windows;

public class MainWindow : Window, IDisposable
{
    // The 6 base weather types Weatherman's own quick-set list uses. Matched
    // by name against whatever the game's own Weather sheet actually reports
    // rather than hardcoded IDs, so this doesn't drift if IDs ever shift.
    private static readonly string[] CommonWeatherNames =
    {
        "Clear Skies", "Fair Skies", "Clouds", "Fog", "Rain", "Snow",
    };

    // Below this per-column width, a 3-column row falls back to stacking
    // everything vertically instead -- avoids columns squeezing down to
    // nothing/overlapping on a narrow window.
    private const float MinColumnWidth = 170f;

    private readonly Plugin plugin;

    // Cached once from Plugin.ZoneList/WeatherList (built once at plugin load)
    // rather than rebuilt every Draw() call.
    private readonly string[] zoneNames;
    private readonly uint?[] zoneIds;
    private readonly string[] weatherNames;
    private readonly byte?[] weatherIds;

    private int selectedZoneIndex;

    // The Advanced Weather dropdown's own single selection -- used directly
    // when useCurrentWeather is on, and as the fallback when none of the
    // quick-pick checkboxes below are checked.
    private int selectedWeatherIndex;
    private readonly bool[] commonWeatherChecked = new bool[CommonWeatherNames.Length];

    private string zoneFilter = string.Empty;
    private string weatherFilter = string.Empty;
    private string rulesFilter = string.Empty;

    private bool useCurrentZone = true;
    private bool useCurrentWeather = true;
    private bool useCurrentTimeOfDay = true;

    private bool dawnChecked;
    private bool dayChecked;
    private bool duskChecked;
    private bool nightChecked;

    private string presetPathInput = string.Empty;
    private string labelInput = string.Empty;
    private string newCollectionNameInput = string.Empty;

    // Set when "Edit" is clicked on a saved rule -- the original is removed
    // immediately and its values loaded back into the form above, so "editing"
    // is just "repopulate the add-rule form, then Add rule again."
    private bool editingExistingRule;

    public MainWindow(Plugin plugin) : base("Presettingway###PresettingwayMain")
    {
        this.plugin = plugin;

        Size = new Vector2(700, 720);
        SizeCondition = ImGuiCond.FirstUseEver;

        zoneNames = new[] { "(Any zone)" }.Concat(plugin.ZoneList.Select(z => $"{z.Name} ({z.Id})")).ToArray();
        zoneIds = new uint?[] { null }.Concat(plugin.ZoneList.Select(z => (uint?)z.Id)).ToArray();

        weatherNames = new[] { "(Any weather)" }.Concat(plugin.WeatherList.Select(w => $"{w.Name} ({w.Id})")).ToArray();
        weatherIds = new byte?[] { null }.Concat(plugin.WeatherList.Select(w => (byte?)w.Id)).ToArray();
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("PresettingwayTabs"))
        {
            if (ImGui.BeginTabItem("Status & Add Rule"))
            {
                DrawStatus();
                ImGui.Separator();
                DrawAddRuleForm();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Rules & Collections"))
            {
                DrawCollectionsSection();
                ImGui.Separator();
                DrawRulesList();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// Always visible above the tabs, rather than buried inside whichever tab
    /// happens to be open: Settings, and the Weatherman checkbox (moved here
    /// from Settings since it's something you'll actually flip during a
    /// session, not a one-time setup choice). Deliberately no descriptive
    /// text under the checkbox -- just the checkbox, right-aligned in the
    /// remaining space next to Settings.
    /// </summary>
    private void DrawHeader()
    {
        if (ImGui.Button("Settings"))
            plugin.OpenSettings();

        ImGui.SameLine();
        const string weathermanLabel = "Use Weatherman";
        var checkboxWidth = ImGui.CalcTextSize(weathermanLabel).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > checkboxWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - checkboxWidth);

        var checkWeatherman = plugin.Configuration.CheckWeathermanOverrides;
        if (ImGui.Checkbox(weathermanLabel, ref checkWeatherman))
        {
            plugin.Configuration.CheckWeathermanOverrides = checkWeatherman;
            plugin.Configuration.Save();
        }
    }

    /// <summary>
    /// Lets you see and switch which collection (or Local mode) is active,
    /// and create new ones, without going into Settings -- this is meant to
    /// be used often as a collection grows, unlike the mode choice itself
    /// (Local vs Collection at all), which stays a Settings-level decision.
    /// </summary>
    private void DrawCollectionsSection()
    {
        var config = plugin.Configuration;
        var isCollection = config.RulesMode == RulesMode.Collection;

        ImGui.TextUnformatted(isCollection
            ? $"Active: Collection \"{config.ActiveCollectionName}\""
            : "Active: Local (personal) rules");

        if (string.IsNullOrWhiteSpace(config.PresetsFolder))
        {
            ImGui.TextWrapped("Set a presets folder in Settings first -- collections live under it and have nowhere to go without one.");
            return;
        }

        var collections = plugin.ListCollections().ToArray();
        if (collections.Length > 0)
        {
            var currentIndex = isCollection ? Array.IndexOf(collections, config.ActiveCollectionName) : -1;
            var preview = currentIndex >= 0 ? collections[currentIndex] : "(select a collection)";

            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Active collection", preview))
            {
                foreach (var name in collections)
                {
                    if (ImGui.Selectable(name, name == config.ActiveCollectionName && isCollection))
                        plugin.SwitchToCollection(name);
                }
                ImGui.EndCombo();
            }

            if (isCollection)
            {
                ImGui.SameLine();
                if (ImGui.Button("Switch to Local"))
                    plugin.SwitchToLocalMode();
            }
        }
        else if (isCollection)
        {
            // Shouldn't normally happen (active collection but none listed --
            // e.g. its folder got deleted outside Presettingway) but keep an
            // escape hatch visible rather than a dead end.
            if (ImGui.Button("Switch to Local"))
                plugin.SwitchToLocalMode();
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##NewCollectionName", "New collection name...", ref newCollectionNameInput, 64);
        ImGui.SameLine();
        if (ImGui.Button("Make New Collection"))
        {
            if (plugin.TryCreateCollection(newCollectionNameInput))
            {
                Plugin.ChatGui.Print($"[Presettingway] Created and switched to collection \"{plugin.Configuration.ActiveCollectionName}\".");
                newCollectionNameInput = string.Empty;
            }
        }
    }

    /// <summary>
    /// Three side-by-side sections when there's room, stacked vertically when
    /// there isn't -- rather than letting a narrow window squeeze real ImGui
    /// columns down until content overlaps or gets cut off.
    /// </summary>
    private static void DrawThreeColumns(string id, Action left, Action center, Action right)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail / 3f < MinColumnWidth)
        {
            left();
            center();
            right();
            return;
        }

        ImGui.Columns(3, id, false);
        left();
        ImGui.NextColumn();
        center();
        ImGui.NextColumn();
        right();
        ImGui.Columns(1);
    }

    private void DrawStatus()
    {
        var w = plugin.Watcher;

        DrawThreeColumns("StatusRow1",
            () => ImGui.TextUnformatted($"Zone: {plugin.GetZoneName(w.CurrentTerritoryId)} ({w.CurrentTerritoryId})"),
            () => ImGui.TextUnformatted($"Weather: {plugin.GetWeatherName(w.CurrentWeatherId)} ({w.CurrentWeatherId})"),
            () => ImGui.TextUnformatted($"Time of day: {w.CurrentTimeOfDay}  (Eorzea hour {w.CurrentEorzeaHour:F2})"));

        var (effTerritoryId, effWeatherId, effTimeOfDay) = plugin.GetEffectiveState();
        var rule = plugin.RuleEngine.Resolve(effTerritoryId, effWeatherId, effTimeOfDay);
        var resolvedLabel = rule?.PresetPath is { Length: > 0 } presetPath ? Path.GetFileName(presetPath) : "(no matching rule)";

        DrawThreeColumns("StatusRow2",
            () => ImGui.TextUnformatted($"Resolved preset: {resolvedLabel}"),
            () =>
            {
                if (plugin.WeathermanWeatherOverrideActive == true)
                {
                    var label = plugin.WeathermanDisplayedWeatherId.HasValue
                        ? plugin.GetWeatherName(plugin.WeathermanDisplayedWeatherId.Value)
                        : "(couldn't be read — /xllog)";
                    ImGui.TextUnformatted($"Weatherman weather: {label}");
                }
            },
            () =>
            {
                if (plugin.WeathermanTimeOverrideActive == true)
                {
                    var label = plugin.WeathermanDisplayedTimeOfDay.HasValue
                        ? plugin.WeathermanDisplayedTimeOfDay.Value.ToString()
                        : "(couldn't be read — /xllog)";
                    ImGui.TextUnformatted($"Weatherman time of day: {label}");
                }
            });

        // Only warn when we're silently falling back -- i.e. Weatherman says an
        // override is active but we couldn't actually read its value. When
        // everything's working, the lines above already say what's going on.
        var weatherFallback = plugin.WeathermanWeatherOverrideActive == true && !plugin.WeathermanDisplayedWeatherId.HasValue;
        var timeFallback = plugin.WeathermanTimeOverrideActive == true && !plugin.WeathermanDisplayedTimeOfDay.HasValue;
        if (weatherFallback || timeFallback)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                "Weatherman override active but its value couldn't be read — falling back to real state for now. " +
                "Check /xllog (likely an IPC method name/signature mismatch against your fork).");
        }
    }

    private void DrawAddRuleForm()
    {
        ImGui.TextUnformatted(editingExistingRule ? "Edit rule (Add rule below to save changes)" : "Add a rule");

        DrawThreeColumns("AddRuleCheckboxes",
            () => ImGui.Checkbox("Use current zone", ref useCurrentZone),
            () => ImGui.Checkbox("Use current weather", ref useCurrentWeather),
            () => ImGui.Checkbox("Use current time of day", ref useCurrentTimeOfDay));

        // Zone/weather/time pickers are always visible rather than hidden
        // behind unchecking "use current" -- just disabled (greyed out) when
        // "use current" is checked. Weather quick-picks are now checkboxes,
        // not buttons: checking more than one creates one rule per checked
        // weather, crossed with however many time-of-day boxes are checked
        // (2 weathers x 2 times = 4 rules) -- see BuildRulePreviewText, which
        // always states the actual count so this isn't a surprise.
        DrawThreeColumns("AddRulePickersRowA",
            () =>
            {
                ImGui.BeginDisabled(useCurrentZone);
                ImGui.SetNextItemWidth(220);
                if (zoneNames.Length > 1)
                    DrawFilterableCombo("##ZonePicker", zoneNames, ref selectedZoneIndex, ref zoneFilter);
                else
                    ImGui.TextUnformatted("(zone list unavailable)");
                ImGui.EndDisabled();
            },
            () =>
            {
                ImGui.BeginDisabled(useCurrentWeather);
                DrawWeatherCheckbox(0);
                ImGui.SameLine();
                DrawWeatherCheckbox(1);
                ImGui.SameLine();
                DrawWeatherCheckbox(2);
                ImGui.EndDisabled();
            },
            () =>
            {
                ImGui.BeginDisabled(useCurrentTimeOfDay);
                ImGui.Checkbox("Dawn", ref dawnChecked);
                ImGui.SameLine();
                ImGui.Checkbox("Day", ref dayChecked);
                ImGui.EndDisabled();
            });

        DrawThreeColumns("AddRulePickersRowB",
            () => { },
            () =>
            {
                ImGui.BeginDisabled(useCurrentWeather);
                DrawWeatherCheckbox(3);
                ImGui.SameLine();
                DrawWeatherCheckbox(4);
                ImGui.SameLine();
                DrawWeatherCheckbox(5);
                ImGui.EndDisabled();
            },
            () =>
            {
                ImGui.BeginDisabled(useCurrentTimeOfDay);
                ImGui.Checkbox("Dusk", ref duskChecked);
                ImGui.SameLine();
                ImGui.Checkbox("Night", ref nightChecked);
                ImGui.EndDisabled();
            });

        // Advanced Weather: no longer hidden behind a collapsing header --
        // always visible, centered under the weather column, width-capped so
        // it doesn't stretch. Only actually used when none of the quick-pick
        // checkboxes above are checked (see ResolveSelectedWeatherIds).
        DrawThreeColumns("AddRulePickersRowC",
            () => { },
            () =>
            {
                ImGui.BeginDisabled(useCurrentWeather);
                const float dropdownWidth = 220f;
                var colWidth = ImGui.GetContentRegionAvail().X;
                var offset = Math.Max(0f, (colWidth - dropdownWidth) / 2f);
                if (offset > 0f)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                ImGui.SetNextItemWidth(dropdownWidth);
                if (weatherNames.Length > 1)
                    DrawFilterableCombo("Advanced Weather", weatherNames, ref selectedWeatherIndex, ref weatherFilter);
                else
                    ImGui.TextUnformatted("(unavailable)");
                ImGui.EndDisabled();
            },
            () => { });

        ImGui.Separator();

        // --- Preset path ---
        ImGui.InputText("Preset path", ref presetPathInput, 512);
        if (ImGui.Button("Use current preset"))
            TryFillCurrentPreset();
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            // Filter format below is the standard convention for this class of
            // ImGui file dialog (goatcorp's own port, closely related to the
            // widely-used aiekick/ImGuiFileDialog C++ library) but isn't
            // independently verified against Dalamud's exact parser this
            // session -- harmless if slightly off, since the dialog's own
            // filter dropdown still lets you pick "all files" regardless.
            var startPath = string.IsNullOrWhiteSpace(plugin.Configuration.PresetsFolder) ? null : plugin.Configuration.PresetsFolder;
            plugin.FileDialogManager.OpenFileDialog(
                "Select a ReShade preset",
                ".ini",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        presetPathInput = paths[0];
                },
                1,
                startPath);
        }

        if (!string.IsNullOrWhiteSpace(plugin.Configuration.PresetsFolder))
        {
            ImGui.SameLine();
            if (ImGui.Button("Insert presets folder"))
                presetPathInput = plugin.Configuration.PresetsFolder.TrimEnd('\\', '/') + "\\";
        }

        ImGui.InputText("Label (optional)", ref labelInput, 128);

        ImGui.TextWrapped(BuildRulePreviewText());

        if (ImGui.Button(editingExistingRule ? "Save changes" : "Add rule"))
            AddRuleFromForm();

        if (editingExistingRule)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                ResetForm();
        }
    }

    private void DrawWeatherCheckbox(int commonWeatherIndex) =>
        ImGui.Checkbox(CommonWeatherNames[commonWeatherIndex], ref commonWeatherChecked[commonWeatherIndex]);

    /// <summary>
    /// Checked quick-pick boxes win, resolved to real weather IDs by matching
    /// their names against the actual sheet data (not hardcoded IDs). Falls
    /// back to whatever the Advanced Weather dropdown currently has selected
    /// if none are checked -- that dropdown's own default is "(Any weather)",
    /// i.e. null, so an untouched form still means "any weather" as before.
    /// </summary>
    private List<byte?> ResolveSelectedWeatherIds()
    {
        var result = new List<byte?>();
        for (var i = 0; i < CommonWeatherNames.Length; i++)
        {
            if (!commonWeatherChecked[i])
                continue;

            var idx = Array.FindIndex(weatherNames, n => n.StartsWith(CommonWeatherNames[i], StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                result.Add(weatherIds[idx]);
            else
                Plugin.ChatGui.Print($"[Presettingway] Couldn't find '{CommonWeatherNames[i]}' in the weather sheet — skipped.");
        }

        if (result.Count == 0)
            result.Add(weatherIds[Math.Clamp(selectedWeatherIndex, 0, weatherIds.Length - 1)]);

        return result;
    }

    /// <summary>
    /// One consolidated summary of what "Add rule" will actually create,
    /// replacing the old scattered "-> X" feedback lines and "Selected:" text
    /// that used to sit under each individual control. Explicitly states the
    /// rule count whenever it's more than one, since checking multiple
    /// weather/time boxes multiplies out (2 weathers x 2 times = 4 rules) and
    /// that's exactly the kind of thing that shouldn't be a surprise.
    /// </summary>
    private string BuildRulePreviewText()
    {
        var w = plugin.Watcher;
        var (_, effWeatherId, effTimeOfDay) = plugin.GetEffectiveState();

        var territoryId = useCurrentZone
            ? w.CurrentTerritoryId
            : zoneIds[Math.Clamp(selectedZoneIndex, 0, zoneIds.Length - 1)];
        var zoneLabel = territoryId.HasValue ? plugin.GetZoneName(territoryId.Value) : "any zone";

        List<byte?> weatherIdsToUse;
        string weatherLabel;
        if (useCurrentWeather)
        {
            weatherIdsToUse = new List<byte?> { effWeatherId };
            weatherLabel = plugin.GetWeatherName(effWeatherId);
        }
        else
        {
            weatherIdsToUse = ResolveSelectedWeatherIds();
            weatherLabel = string.Join("/", weatherIdsToUse.Select(id => id.HasValue ? plugin.GetWeatherName(id.Value) : "any weather"));
        }

        List<TimeOfDay?> times;
        string timeLabel;
        if (useCurrentTimeOfDay)
        {
            times = new List<TimeOfDay?> { effTimeOfDay };
            timeLabel = effTimeOfDay.ToString();
        }
        else
        {
            times = new List<TimeOfDay?>();
            var timeNames = new List<string>();
            if (dawnChecked) { times.Add(TimeOfDay.Dawn); timeNames.Add("Dawn"); }
            if (dayChecked) { times.Add(TimeOfDay.Day); timeNames.Add("Day"); }
            if (duskChecked) { times.Add(TimeOfDay.Dusk); timeNames.Add("Dusk"); }
            if (nightChecked) { times.Add(TimeOfDay.Night); timeNames.Add("Night"); }
            if (times.Count == 0) { times.Add(null); timeNames.Add("any time"); }
            timeLabel = string.Join("/", timeNames);
        }

        var presetLabel = string.IsNullOrWhiteSpace(presetPathInput)
            ? "(no preset selected)"
            : Path.GetFileName(Plugin.CleanPathInput(presetPathInput));

        var totalRules = weatherIdsToUse.Count * times.Count;
        var prefix = totalRules > 1 ? $"Creating {totalRules} rules for:" : "Creating rule for:";

        return $"{prefix} '{zoneLabel}', '{weatherLabel}', '{timeLabel}' using '{presetLabel}'";
    }

    /// <summary>
    /// A combo box with a type-to-filter search box as its first row. Two fixes
    /// from the first version: the search box now grabs keyboard focus the
    /// instant the combo opens (previously you had to click precisely into a
    /// tiny, unlabeled field before typing did anything), and it now has visible
    /// placeholder-style hint text so it's obvious it's a search box at all.
    /// Returns true the frame the selection changes.
    /// </summary>
    private static bool DrawFilterableCombo(string label, string[] itemLabels, ref int selectedIndex, ref string filterText)
    {
        var changed = false;
        var clampedIndex = Math.Clamp(selectedIndex, 0, itemLabels.Length - 1);
        var preview = itemLabels[clampedIndex];

        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint($"##{label}Filter", "Type to search...", ref filterText, 128);
            ImGui.Separator();

            for (var i = 0; i < itemLabels.Length; i++)
            {
                if (!string.IsNullOrEmpty(filterText) &&
                    itemLabels[i].IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var isSelected = i == selectedIndex;
                if (ImGui.Selectable(itemLabels[i], isSelected))
                {
                    selectedIndex = i;
                    changed = true;
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void TryFillCurrentPreset()
    {
        var path = plugin.TryReadCurrentReShadePresetPath();
        if (path is null)
        {
            Plugin.ChatGui.Print("[Presettingway] Couldn't read a current preset from ReShade.ini — set the correct path in Settings (/pway config), or type the preset path in manually.");
            return;
        }

        presetPathInput = path;
    }

    private void AddRuleFromForm()
    {
        var cleanedPath = Plugin.CleanPathInput(presetPathInput);
        if (string.IsNullOrWhiteSpace(cleanedPath))
        {
            Plugin.ChatGui.Print("[Presettingway] Enter a preset path before adding a rule.");
            return;
        }

        var w = plugin.Watcher;
        var (_, effWeatherId, effTimeOfDay) = plugin.GetEffectiveState();

        uint? territoryId = useCurrentZone
            ? w.CurrentTerritoryId // zone is never Weatherman-overridden, always real
            : zoneIds[Math.Clamp(selectedZoneIndex, 0, zoneIds.Length - 1)];

        // Weatherman's value if it has an active, readable override; real
        // weather otherwise -- only relevant when "use current weather" is on,
        // since checked quick-pick boxes are explicit choices regardless.
        List<byte?> weatherIdsToUse = useCurrentWeather
            ? new List<byte?> { effWeatherId }
            : ResolveSelectedWeatherIds();

        List<TimeOfDay?> times;
        if (useCurrentTimeOfDay)
        {
            times = new List<TimeOfDay?> { effTimeOfDay };
        }
        else
        {
            // Checking multiple boxes creates one rule per checked box (same
            // zone/preset); checking none means "any time" (one rule with a
            // null filter). Crossed with weatherIdsToUse below, so 2 checked
            // weathers x 2 checked times makes 4 rules total.
            times = new List<TimeOfDay?>();
            if (dawnChecked) times.Add(TimeOfDay.Dawn);
            if (dayChecked) times.Add(TimeOfDay.Day);
            if (duskChecked) times.Add(TimeOfDay.Dusk);
            if (nightChecked) times.Add(TimeOfDay.Night);
            if (times.Count == 0) times.Add(null);
        }

        var resolvedPath = ResolvePresetPathForSave(cleanedPath);
        var label = string.IsNullOrWhiteSpace(labelInput) ? null : Plugin.CleanPathInput(labelInput);

        foreach (var weatherId in weatherIdsToUse)
        {
            foreach (var time in times)
            {
                plugin.AddRule(new PresetRule
                {
                    TerritoryId = territoryId,
                    WeatherId = weatherId,
                    TimeOfDayFilter = time,
                    PresetPath = resolvedPath,
                    Label = label,
                });
            }
        }

        ResetForm();
    }

    private void ResetForm()
    {
        presetPathInput = string.Empty;
        labelInput = string.Empty;
        editingExistingRule = false;
    }

    private string ResolvePresetPathForSave(string path)
    {
        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(plugin.Configuration.PresetsFolder))
            return Path.Combine(plugin.Configuration.PresetsFolder, path);
        return path;
    }

    private void DrawRulesList()
    {
        ImGui.TextUnformatted($"Saved rules ({plugin.RulesEditable.Count})");

        if (plugin.RulesEditable.Count == 0)
        {
            ImGui.TextUnformatted("(none yet)");
            return;
        }

        if (plugin.RulesEditable.Count > 5)
            ImGui.InputTextWithHint("##RulesFilter", "Filter (zone, weather, time, label, or path)...", ref rulesFilter, 128);

        var anyShown = false;
        for (var i = plugin.RulesEditable.Count - 1; i >= 0; i--)
        {
            var rule = plugin.RulesEditable[i];
            var description = DescribeRule(rule);

            if (!string.IsNullOrEmpty(rulesFilter) && description.IndexOf(rulesFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            anyShown = true;
            ImGui.PushID(i);

            // Buttons first, description after: keeps them visible regardless of
            // how long the preset path is, instead of the path pushing "Remove"
            // off the edge of the window.
            if (ImGui.Button("Edit"))
                LoadRuleIntoForm(rule);
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
                plugin.RemoveRule(rule);
            ImGui.SameLine();
            ImGui.TextWrapped(description);

            ImGui.PopID();
        }

        if (!anyShown && !string.IsNullOrEmpty(rulesFilter))
            ImGui.TextUnformatted("(no rules match that filter)");
    }

    private void LoadRuleIntoForm(PresetRule rule)
    {
        useCurrentZone = false;
        useCurrentWeather = false;
        useCurrentTimeOfDay = false;

        var zoneIdx = Array.IndexOf(zoneIds, rule.TerritoryId);
        selectedZoneIndex = zoneIdx >= 0 ? zoneIdx : 0;

        var weatherIdx = Array.IndexOf(weatherIds, rule.WeatherId);
        selectedWeatherIndex = weatherIdx >= 0 ? weatherIdx : 0;
        Array.Clear(commonWeatherChecked); // editing loads into the single Advanced picker, not the checkboxes

        dawnChecked = rule.TimeOfDayFilter == TimeOfDay.Dawn;
        dayChecked = rule.TimeOfDayFilter == TimeOfDay.Day;
        duskChecked = rule.TimeOfDayFilter == TimeOfDay.Dusk;
        nightChecked = rule.TimeOfDayFilter == TimeOfDay.Night;

        presetPathInput = rule.PresetPath;
        labelInput = rule.Label ?? string.Empty;
        editingExistingRule = true;

        // Editing = remove the original, repopulate the form, and "Add rule"
        // (now labeled "Save changes") re-adds it -- simplest way to get real
        // editing without a second, parallel form of rule-field widgets.
        plugin.RemoveRule(rule);
    }

    private string DescribeRule(PresetRule rule)
    {
        var zone = rule.TerritoryId.HasValue ? plugin.GetZoneName(rule.TerritoryId.Value) : "any zone";
        var weather = rule.WeatherId.HasValue ? plugin.GetWeatherName(rule.WeatherId.Value) : "any weather";
        var time = rule.TimeOfDayFilter.HasValue ? rule.TimeOfDayFilter.Value.ToString() : "any time";
        var label = string.IsNullOrWhiteSpace(rule.Label) ? string.Empty : $" \"{rule.Label}\"";
        return $"{zone} / {weather} / {time} -> {rule.PresetPath}{label}";
    }

    public void Dispose() { }
}

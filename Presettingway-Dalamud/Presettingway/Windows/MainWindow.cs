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

    private readonly Plugin plugin;

    // Cached once from Plugin.ZoneList/WeatherList (built once at plugin load)
    // rather than rebuilt every Draw() call.
    private readonly string[] zoneNames;
    private readonly uint?[] zoneIds;
    private readonly string[] weatherNames;
    private readonly byte?[] weatherIds;

    private int selectedZoneIndex;
    private int selectedWeatherIndex;
    private string zoneFilter = string.Empty;
    private string weatherFilter = string.Empty;
    private string rulesFilter = string.Empty;
    private string? lastWeatherQuickPick;

    private bool useCurrentZone = true;
    private bool useCurrentWeather = true;
    private bool useCurrentTimeOfDay = true;

    private bool dawnChecked;
    private bool dayChecked;
    private bool duskChecked;
    private bool nightChecked;

    private string presetPathInput = string.Empty;
    private string labelInput = string.Empty;

    // Set when "Edit" is clicked on a saved rule -- the original is removed
    // immediately and its values loaded back into the form above, so "editing"
    // is just "repopulate the add-rule form, then Add rule again."
    private bool editingExistingRule;

    public MainWindow(Plugin plugin) : base("Presettingway###PresettingwayMain")
    {
        this.plugin = plugin;

        Size = new Vector2(640, 680);
        SizeCondition = ImGuiCond.FirstUseEver;

        zoneNames = new[] { "(Any zone)" }.Concat(plugin.ZoneList.Select(z => $"{z.Name} ({z.Id})")).ToArray();
        zoneIds = new uint?[] { null }.Concat(plugin.ZoneList.Select(z => (uint?)z.Id)).ToArray();

        weatherNames = new[] { "(Any weather)" }.Concat(plugin.WeatherList.Select(w => $"{w.Name} ({w.Id})")).ToArray();
        weatherIds = new byte?[] { null }.Concat(plugin.WeatherList.Select(w => (byte?)w.Id)).ToArray();
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawAddRuleForm();
        ImGui.Separator();
        DrawRulesList();
    }

    private void DrawStatus()
    {
        var w = plugin.Watcher;

        if (ImGui.Button("Settings"))
            plugin.OpenSettings();

        ImGui.TextUnformatted($"Zone: {plugin.GetZoneName(w.CurrentTerritoryId)} ({w.CurrentTerritoryId})");
        ImGui.TextUnformatted($"Weather: {plugin.GetWeatherName(w.CurrentWeatherId)} ({w.CurrentWeatherId})");
        ImGui.TextUnformatted($"Time of day: {w.CurrentTimeOfDay}  (Eorzea hour {w.CurrentEorzeaHour:F2})");

        // Weatherman awareness: show its actual values right alongside the real
        // ones whenever an override is active and readable, rather than a vague
        // "won't match" warning. These are also what's actually driving rule
        // resolution below -- see Plugin.GetEffectiveState().
        if (plugin.WeathermanWeatherOverrideActive == true)
        {
            var label = plugin.WeathermanDisplayedWeatherId.HasValue
                ? plugin.GetWeatherName(plugin.WeathermanDisplayedWeatherId.Value)
                : "(active, but couldn't be read — see /xllog)";
            ImGui.TextUnformatted($"Weatherman weather: {label}");
        }

        if (plugin.WeathermanTimeOverrideActive == true)
        {
            var label = plugin.WeathermanDisplayedTimeOfDay.HasValue
                ? plugin.WeathermanDisplayedTimeOfDay.Value.ToString()
                : "(active, but couldn't be read — see /xllog)";
            ImGui.TextUnformatted($"Weatherman time of day: {label}");
        }

        var (effTerritoryId, effWeatherId, effTimeOfDay) = plugin.GetEffectiveState();
        var rule = plugin.RuleEngine.Resolve(effTerritoryId, effWeatherId, effTimeOfDay);
        ImGui.TextUnformatted($"Resolved preset: {(rule?.PresetPath ?? "(no matching rule)")}");

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
        var w = plugin.Watcher;
        var (_, effWeatherId, effTimeOfDay) = plugin.GetEffectiveState();
        ImGui.TextUnformatted(editingExistingRule ? "Edit rule (Add rule below to save changes)" : "Add a rule");

        // --- Simple: zone ---
        ImGui.Checkbox("Use current zone", ref useCurrentZone);
        if (useCurrentZone)
            ImGui.TextUnformatted($"    -> {plugin.GetZoneName(w.CurrentTerritoryId)}");

        // --- Simple: weather ---
        ImGui.Checkbox("Use current weather", ref useCurrentWeather);
        if (useCurrentWeather)
        {
            // Uses the effective (Weatherman-aware) value, not the raw real
            // reading -- if Weatherman has an active, readable override,
            // "current" should mean what you're actually seeing, same as
            // what actually drives rule resolution/publishing.
            var weathermanNote = plugin.WeathermanWeatherOverrideActive == true && plugin.WeathermanDisplayedWeatherId.HasValue
                ? " (from Weatherman)"
                : string.Empty;
            ImGui.TextUnformatted($"    -> {plugin.GetWeatherName(effWeatherId)}{weathermanNote}");
        }
        else
        {
            ImGui.TextUnformatted("    Quick pick:");
            for (var i = 0; i < CommonWeatherNames.Length; i++)
            {
                if (i > 0)
                    ImGui.SameLine();
                if (ImGui.Button(CommonWeatherNames[i]))
                {
                    var idx = Array.FindIndex(weatherNames, n => n.StartsWith(CommonWeatherNames[i], StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        selectedWeatherIndex = idx;
                        lastWeatherQuickPick = CommonWeatherNames[i];
                    }
                    else
                    {
                        Plugin.ChatGui.Print($"[Presettingway] Couldn't find '{CommonWeatherNames[i]}' in the weather sheet — pick it from Advanced instead.");
                    }
                }
            }

            // Visible confirmation of what clicking a quick-pick button actually
            // did -- previously the only feedback was the Advanced dropdown's
            // preview text, which is invisible while Advanced is collapsed.
            var currentSelectionLabel = weatherNames[Math.Clamp(selectedWeatherIndex, 0, weatherNames.Length - 1)];
            ImGui.TextUnformatted($"    Selected: {currentSelectionLabel}");
        }

        // --- Simple: time of day ---
        ImGui.Checkbox("Use current time of day", ref useCurrentTimeOfDay);
        if (useCurrentTimeOfDay)
        {
            var weathermanNote = plugin.WeathermanTimeOverrideActive == true && plugin.WeathermanDisplayedTimeOfDay.HasValue
                ? " (from Weatherman)"
                : string.Empty;
            ImGui.TextUnformatted($"    -> {effTimeOfDay}{weathermanNote}");
        }
        else
        {
            ImGui.TextUnformatted("    Check any that should use this preset (none checked = any time):");
            ImGui.Checkbox("Dawn", ref dawnChecked);
            ImGui.SameLine();
            ImGui.Checkbox("Day", ref dayChecked);
            ImGui.SameLine();
            ImGui.Checkbox("Dusk", ref duskChecked);
            ImGui.SameLine();
            ImGui.Checkbox("Night", ref nightChecked);
        }

        ImGui.Separator();

        // --- Preset path ---
        ImGui.InputText("Preset path", ref presetPathInput, 512);
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
        ImGui.SameLine();
        if (ImGui.Button("Use current preset"))
            TryFillCurrentPreset();

        if (!string.IsNullOrWhiteSpace(plugin.Configuration.PresetsFolder))
        {
            ImGui.SameLine();
            if (ImGui.Button("Insert presets folder"))
                presetPathInput = plugin.Configuration.PresetsFolder.TrimEnd('\\', '/') + "\\";
        }

        ImGui.InputText("Label (optional)", ref labelInput, 128);

        if (ImGui.Button(editingExistingRule ? "Save changes" : "Add rule"))
            AddRuleFromForm();

        if (editingExistingRule)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit"))
                ResetForm();
        }

        // --- Advanced: exact zone/weather pickers, collapsed by default ---
        if (ImGui.CollapsingHeader("Advanced (exact zone / weather picker)"))
        {
            ImGui.BeginDisabled(useCurrentZone);
            if (zoneNames.Length > 1)
                DrawFilterableCombo("Zone", zoneNames, ref selectedZoneIndex, ref zoneFilter);
            else
                ImGui.TextUnformatted("(Zone list unavailable — check /xllog.)");
            ImGui.EndDisabled();

            ImGui.BeginDisabled(useCurrentWeather);
            if (weatherNames.Length > 1)
                DrawFilterableCombo("Weather", weatherNames, ref selectedWeatherIndex, ref weatherFilter);
            else
                ImGui.TextUnformatted("(Weather list unavailable — check /xllog.)");
            ImGui.EndDisabled();
        }
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
            Plugin.ChatGui.Print("[Presettingway] Couldn't read a current preset from ReShade.ini — set the correct path in Settings (/presettingway config), or type the preset path in manually.");
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

        byte? weatherId = useCurrentWeather
            ? effWeatherId // Weatherman's value if it has an active, readable override; real weather otherwise
            : weatherIds[Math.Clamp(selectedWeatherIndex, 0, weatherIds.Length - 1)];

        List<TimeOfDay?> times;
        if (useCurrentTimeOfDay)
        {
            times = new List<TimeOfDay?> { effTimeOfDay };
        }
        else
        {
            // Checking multiple boxes creates one rule per checked box (same
            // zone/weather/preset); checking none means "any time" (one rule
            // with a null filter).
            times = new List<TimeOfDay?>();
            if (dawnChecked) times.Add(TimeOfDay.Dawn);
            if (dayChecked) times.Add(TimeOfDay.Day);
            if (duskChecked) times.Add(TimeOfDay.Dusk);
            if (nightChecked) times.Add(TimeOfDay.Night);
            if (times.Count == 0) times.Add(null);
        }

        var resolvedPath = ResolvePresetPathForSave(cleanedPath);
        var label = string.IsNullOrWhiteSpace(labelInput) ? null : Plugin.CleanPathInput(labelInput);

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

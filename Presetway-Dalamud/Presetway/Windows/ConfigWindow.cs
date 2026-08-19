using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Presetway.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string reshadeIniPathInput = string.Empty;
    private string presetsFolderInput = string.Empty;
    private string defaultPresetPathInput = string.Empty;
    private bool initialized;

    public ConfigWindow(Plugin plugin) : base("Presetway Settings###PresetwaySettings")
    {
        this.plugin = plugin;
        Size = new Vector2(480, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        // Pull current saved values into the editable text buffers once, the
        // first time the window draws, rather than every frame.
        if (!initialized)
        {
            reshadeIniPathInput = config.ReShadeIniPath;
            presetsFolderInput = config.PresetsFolder;
            defaultPresetPathInput = config.DefaultPresetPath;
            initialized = true;
        }

        ImGui.TextUnformatted("Time-of-day cutoffs (Eorzea hour, 0-24).");
        ImGui.TextUnformatted("Tune these against what you actually see in-game — don't trust anyone's stated numbers, including ours.");
        ImGui.Separator();

        var dawn = (float)config.DawnStartHour;
        var day = (float)config.DayStartHour;
        var dusk = (float)config.DuskStartHour;
        var night = (float)config.NightStartHour;

        var changed = false;
        changed |= ImGui.SliderFloat("Dawn starts", ref dawn, 0f, 24f, "%.1f");
        changed |= ImGui.SliderFloat("Day starts", ref day, 0f, 24f, "%.1f");
        changed |= ImGui.SliderFloat("Dusk starts", ref dusk, 0f, 24f, "%.1f");
        changed |= ImGui.SliderFloat("Night starts", ref night, 0f, 24f, "%.1f");

        if (changed)
        {
            config.DawnStartHour = dawn;
            config.DayStartHour = day;
            config.DuskStartHour = dusk;
            config.NightStartHour = night;
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("ReShade.ini path (the one next to ffxiv_dx11.exe, not a preset file):");
        ImGui.TextUnformatted("Used by the \"Use current preset\" button in the main window.");
        if (ImGui.InputText("##ReShadeIniPath", ref reshadeIniPathInput, 512))
        {
            config.ReShadeIniPath = Plugin.CleanPathInput(reshadeIniPathInput);
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Presets folder (optional -- relative preset paths get resolved against this):");
        if (ImGui.InputText("##PresetsFolder", ref presetsFolderInput, 512))
        {
            config.PresetsFolder = Plugin.CleanPathInput(presetsFolderInput);
            config.Save();
        }

        ImGui.Separator();
        var checkWeatherman = config.CheckWeathermanOverrides;
        if (ImGui.Checkbox("Check for Weatherman overrides", ref checkWeatherman))
        {
            config.CheckWeathermanOverrides = checkWeatherman;
            config.Save();
        }
        ImGui.TextWrapped(
            "Off by default: Presetway won't touch Weatherman at all unless this is checked. " +
            "When on, and Weatherman has an active override, you'll see a warning that the real " +
            "zone/weather/time above may not match what you're actually seeing in-game.");

        ImGui.Separator();
        ImGui.TextUnformatted("Default preset (optional -- used when no rule matches at all):");
        ImGui.TextWrapped(
            "Without this, entering an unconfigured zone just leaves ReShade on whatever preset " +
            "was already active, rather than switching to anything.");
        if (ImGui.InputText("##DefaultPresetPath", ref defaultPresetPathInput, 512))
        {
            config.DefaultPresetPath = Plugin.CleanPathInput(defaultPresetPathInput);
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Rules file: {plugin.Configuration.RulesFilePath}");
        if (ImGui.Button("Reload rules from disk"))
        {
            if (plugin.LoadRules())
                Plugin.ChatGui.Print($"[Presetway] Reloaded — {plugin.RulesEditable.Count} rule(s) now loaded.");
        }
    }

    public void Dispose() { }
}

using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Presettingway.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string reshadeIniPathInput = string.Empty;
    private string presetsFolderInput = string.Empty;
    private string defaultPresetPathInput = string.Empty;
    private bool initialized;

    public ConfigWindow(Plugin plugin) : base("Presettingway Settings###PresettingwaySettings")
    {
        this.plugin = plugin;
        Size = new Vector2(500, 440);
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
        ImGui.TextUnformatted("Tune these against what you see in-game or prefer.");
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
        ImGui.TextWrapped(
            "Must be the path to and including \"\\ReShade.ini\". Default location: " +
            "C:\\Program Files (x86)\\Square Enix\\FINAL FANTASY XIV - A Realm Reborn\\game\\ReShade.ini");
        ImGui.TextUnformatted("Used by the \"Use current preset\" button in the main window.");
        if (ImGui.InputText("##ReShadeIniPath", ref reshadeIniPathInput, 512))
        {
            config.ReShadeIniPath = Plugin.CleanPathInput(reshadeIniPathInput);
            config.Save();
        }
        if (ImGui.Button("Browse...##ReShadeIniBrowse"))
        {
            plugin.FileDialogManager.OpenFileDialog(
                "Select ReShade.ini",
                ".ini",
                (success, path) =>
                {
                    if (!success)
                        return;
                    reshadeIniPathInput = path;
                    config.ReShadeIniPath = path;
                    config.Save();
                });
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Presets folder (optional -- relative preset paths get resolved against this):");
        if (ImGui.InputText("##PresetsFolder", ref presetsFolderInput, 512))
        {
            config.PresetsFolder = Plugin.CleanPathInput(presetsFolderInput);
            config.Save();
            plugin.LoadRules(); // switching folders should immediately reflect whichever rules file now applies
        }
        if (ImGui.Button("Browse...##PresetsFolderBrowse"))
        {
            plugin.FileDialogManager.OpenFolderDialog(
                "Select your presets folder",
                (success, path) =>
                {
                    if (!success)
                        return;
                    presetsFolderInput = path;
                    config.PresetsFolder = path;
                    config.Save();
                    plugin.LoadRules();
                });
        }
        if (!string.IsNullOrWhiteSpace(config.PresetsFolder))
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy path##PresetsFolderCopy"))
                ImGui.SetClipboardText(config.PresetsFolder);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Rules mode:");
        var isCollection = config.RulesMode == RulesMode.Collection;

        if (ImGui.RadioButton("Local (personal, %appdata%)", !isCollection))
        {
            plugin.SwitchToLocalMode();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Collection (shareable folder)", isCollection))
        {
            // Only actually flips the mode if a collection already exists to
            // switch into -- creating the first one is a Main Window action
            // ("Make New Collection"), not something this radio button can
            // do on its own without a name to give it.
            if (!isCollection)
            {
                var existing = plugin.ListCollections();
                if (existing.Count > 0)
                    plugin.SwitchToCollection(existing[0]);
                else
                    Plugin.ChatGui.Print("[Presettingway] No collections exist yet -- use \"Make New Collection\" in the main window first.");
            }
        }
        ImGui.TextWrapped(isCollection
            ? $"Active collection: \"{config.ActiveCollectionName}\". Rules and tagged preset copies both live in " +
              "PresetsFolder\\Presettingway\\ under that name -- zip that one subfolder to share it. " +
              "Manage and switch between collections from the main window."
            : "Rules live in your personal %appdata% file, same as always. No preset copies are saved, " +
              "and PresetsFolder\\Presettingway\\ is left alone entirely.");

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
        var rulesPath = plugin.ResolveRulesPath();
        ImGui.TextUnformatted(isCollection ? $"Rules file (collection): {rulesPath}" : $"Rules file (local): {rulesPath}");
        if (ImGui.Button("Copy path##RulesFileCopy"))
            ImGui.SetClipboardText(rulesPath);
        ImGui.SameLine();
        if (ImGui.Button("Reload rules from disk"))
        {
            if (plugin.LoadRules())
                Plugin.ChatGui.Print($"[Presettingway] Reloaded — {plugin.RulesEditable.Count} rule(s) now loaded.");
        }
    }

    public void Dispose() { }
}

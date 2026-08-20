using System;
using Dalamud.Configuration;

namespace Presettingway;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Path to the JSON rules file. Relative paths are resolved against this
    /// plugin's own config directory (…/pluginConfigs/Presettingway/) so the
    /// example file that ships next to the DLL is picked up automatically
    /// the first time you copy it there.
    /// </summary>
    public string RulesFilePath { get; set; } = "presettingway-rules.json";

    /// <summary>
    /// Full path to ReShade's own .ini (the one next to ffxiv_dx11.exe, not a
    /// preset file). Used by the "Use current preset" button to read whatever
    /// preset ReShade currently has active.
    /// </summary>
    public string ReShadeIniPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional folder your presets live in. When set, non-rooted paths typed
    /// into the preset path box are resolved against this folder, and the
    /// "Insert presets folder" button prefills it for you.
    /// </summary>
    public string PresetsFolder { get; set; } = string.Empty;

    /// <summary>
    /// Applied when no rule matches at all -- without this, Presettingway simply
    /// doesn't publish anything for unmatched zones, so ReShade just stays on
    /// whatever preset was last active. Empty means "no fallback" (current
    /// behavior preserved for anyone who doesn't set one).
    /// </summary>
    public string DefaultPresetPath { get; set; } = string.Empty;

    /// <summary>
    /// Off by default and fully manual: when false, Presettingway never touches
    /// Weatherman at all -- no InstalledPlugins scan, no IPC call, nothing.
    /// When true, it checks Weatherman's IsWeatherCustom()/IsTimeCustom() and
    /// shows a warning banner when either is active (see Plugin.RefreshWeathermanStatus).
    /// </summary>
    public bool CheckWeathermanOverrides { get; set; } = false;

    // Eorzea-hour (0-24, wraps at 24) cutoffs for each time-of-day bucket.
    // Deliberately NOT hardcoded elsewhere: nobody's stated numbers (Google's,
    // mine, or anyone else's) should be trusted over what you actually observe
    // in-game, so these are meant to be tuned from the settings window.
    public double DawnStartHour { get; set; } = 5.0;
    public double DayStartHour { get; set; } = 7.0;
    public double DuskStartHour { get; set; } = 17.0;
    public double NightStartHour { get; set; } = 19.0;

    /// <summary>
    /// Resolves a raw Eorzea hour (0-24) into a TimeOfDay bucket using the
    /// boundaries above. Handles the Night bucket wrapping past midnight.
    /// </summary>
    public TimeOfDay ResolveTimeOfDay(double eorzeaHour)
    {
        eorzeaHour = ((eorzeaHour % 24) + 24) % 24;

        bool InRange(double start, double end) =>
            start <= end
                ? eorzeaHour >= start && eorzeaHour < end
                : eorzeaHour >= start || eorzeaHour < end; // wraps past midnight

        if (InRange(NightStartHour, DawnStartHour)) return TimeOfDay.Night;
        if (InRange(DawnStartHour, DayStartHour)) return TimeOfDay.Dawn;
        if (InRange(DayStartHour, DuskStartHour)) return TimeOfDay.Day;
        return TimeOfDay.Dusk;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

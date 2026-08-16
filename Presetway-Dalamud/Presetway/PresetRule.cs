using System.Text.Json.Serialization;

namespace Presetway;

/// <summary>
/// A single zone/weather/time-of-day -> ReShade preset mapping. Loaded from
/// the rules JSON file. A null field means "matches anything" for that
/// dimension, so you can layer specific rules over broad fallbacks.
/// </summary>
public sealed class PresetRule
{
    /// <summary>Territory (zone) sheet row id. Null = matches any zone.</summary>
    public uint? TerritoryId { get; set; }

    /// <summary>Weather sheet row id. Null = matches any weather.</summary>
    public byte? WeatherId { get; set; }

    /// <summary>
    /// Eorzea time-of-day bucket. Null = matches any time.
    /// Named "TimeOfDayFilter" rather than "TimeOfDay" to avoid colliding
    /// with the TimeOfDay type name itself; the JSON key stays "timeOfDay".
    /// </summary>
    [JsonPropertyName("timeOfDay")]
    public TimeOfDay? TimeOfDayFilter { get; set; }

    /// <summary>Absolute path to the ReShade .ini preset to switch to.</summary>
    public string PresetPath { get; set; } = string.Empty;

    /// <summary>Optional human-readable label shown in status output / logs / UI.</summary>
    public string? Label { get; set; }

    /// <summary>More specific rules (more fields set) win over broader ones.</summary>
    internal int Specificity =>
        (TerritoryId.HasValue ? 4 : 0) +
        (WeatherId.HasValue ? 2 : 0) +
        (TimeOfDayFilter.HasValue ? 1 : 0);

    public bool Matches(uint territoryId, byte weatherId, TimeOfDay timeOfDay) =>
        (!TerritoryId.HasValue || TerritoryId.Value == territoryId) &&
        (!WeatherId.HasValue || WeatherId.Value == weatherId) &&
        (!TimeOfDayFilter.HasValue || TimeOfDayFilter.Value == timeOfDay);
}

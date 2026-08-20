namespace Presettingway;

/// <summary>
/// Coarse Eorzea-clock bucket. Boundaries between these are configurable
/// (see Configuration.ResolveTimeOfDay) rather than hardcoded, since the
/// "right" cutoffs are a matter of taste/observation, not a fixed game value.
/// </summary>
public enum TimeOfDay
{
    Dawn,
    Day,
    Dusk,
    Night,
}

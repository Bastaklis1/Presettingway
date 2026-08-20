using System.Collections.Generic;
using System.Linq;

namespace Presettingway;

/// <summary>
/// Holds the current rule set and resolves the best match for a given
/// territory/weather pair. "Best" = most specific: zone+weather beats
/// zone-only beats weather-only beats a catch-all (both null) rule.
/// </summary>
internal sealed class RuleEngine
{
    private List<PresetRule> rules = new();

    public IReadOnlyList<PresetRule> Rules => rules;

    public void SetRules(IEnumerable<PresetRule> newRules) => rules = newRules.ToList();

    public PresetRule? Resolve(uint territoryId, byte weatherId, TimeOfDay timeOfDay) =>
        rules.Where(r => r.Matches(territoryId, weatherId, timeOfDay))
             .OrderByDescending(r => r.Specificity)
             .FirstOrDefault();
}

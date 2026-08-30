namespace Presettingway;

/// <summary>
/// Where rules -- and, in Collection mode, tagged preset copies -- are read
/// from and written to. See Configuration.RulesMode and Plugin.ResolveRulesPath.
/// </summary>
public enum RulesMode
{
    /// <summary>
    /// The original behavior: one rules file per person, stored under this
    /// plugin's own %appdata% config directory (or wherever RulesFilePath
    /// points, if set to an absolute path). Never touches PresetsFolder, and
    /// never saves tagged preset copies.
    /// </summary>
    Local,

    /// <summary>
    /// Rules live inside a named, self-contained folder under
    /// "&lt;PresetsFolder&gt;\Presettingway\&lt;ActiveCollectionName&gt;\",
    /// alongside tagged copies of the presets they reference. Zipping that
    /// one folder up and handing it to someone gives them the exact same
    /// rules and presets, with their own personal Local rules file
    /// completely untouched underneath.
    /// </summary>
    Collection,
}

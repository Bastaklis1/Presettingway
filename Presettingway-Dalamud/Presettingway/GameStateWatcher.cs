using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Presettingway;

/// <summary>
/// Watches territory (zone), weather, and time-of-day state and raises
/// <see cref="StateChanged"/> whenever any of them settle on a new value.
///
/// Design notes:
///  - IClientState.TerritoryChanged fires at the *start* of a zone transition, not
///    once the zone has actually finished loading, so territory changes are debounced
///    a couple seconds before we act on them.
///  - There's no native "weather changed" event, so weather (and time-of-day, which
///    is cheap to check alongside it) is polled on a slow cadence via the framework
///    update tick instead.
///  - Time-of-day comes from the game's actual Eorzea clock
///    (FFXIVClientStructs Framework.Instance()->ClientTime.EorzeaTime, in seconds),
///    not a guessed real-world cadence -- a full Eorzea day is 70 real minutes,
///    so this updates far faster than real-world time-of-day would.
/// </summary>
internal sealed class GameStateWatcher : IDisposable
{
    private const double TerritoryDebounceSeconds = 2.0;
    private const double PollIntervalSeconds = 1.0;

    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    private uint pendingTerritoryId;
    private double territoryDebounceRemaining = -1;
    private double pollAccumulator;
    private long lastTickMs;

    public uint CurrentTerritoryId { get; private set; }
    public byte CurrentWeatherId { get; private set; }
    public TimeOfDay CurrentTimeOfDay { get; private set; }
    public double CurrentEorzeaHour { get; private set; }

    /// <summary>Fired whenever settled territory, weather, or time-of-day changes.</summary>
    public event Action<uint, byte, TimeOfDay>? StateChanged;

    public unsafe GameStateWatcher(IClientState clientState, IFramework framework, IPluginLog log, Configuration configuration)
    {
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;

        lastTickMs = Environment.TickCount64;

        CurrentTerritoryId = clientState.TerritoryType;
        pendingTerritoryId = CurrentTerritoryId;
        CurrentWeatherId = ReadWeatherId();
        CurrentEorzeaHour = ReadEorzeaHour();
        CurrentTimeOfDay = configuration.ResolveTimeOfDay(CurrentEorzeaHour);

        this.clientState.TerritoryChanged += OnTerritoryChanged;
        this.framework.Update += OnFrameworkUpdate;
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        pendingTerritoryId = territoryId;
        territoryDebounceRemaining = TerritoryDebounceSeconds;
        log.Debug($"[Presettingway] Territory changed -> {territoryId}, debouncing {TerritoryDebounceSeconds}s before acting.");
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        var deltaSeconds = (now - lastTickMs) / 1000.0;
        lastTickMs = now;

        // Debounced territory settle.
        if (territoryDebounceRemaining >= 0)
        {
            territoryDebounceRemaining -= deltaSeconds;
            if (territoryDebounceRemaining <= 0)
            {
                territoryDebounceRemaining = -1;
                CurrentTerritoryId = pendingTerritoryId;
                RefreshWeatherAndTime(force: true);
                pollAccumulator = 0;
                return;
            }
        }

        // Weather + time-of-day polling (no native change events exist for either).
        pollAccumulator += deltaSeconds;
        if (pollAccumulator < PollIntervalSeconds)
            return;
        pollAccumulator = 0;

        RefreshWeatherAndTime(force: false);
    }

    private void RefreshWeatherAndTime(bool force)
    {
        var weatherId = ReadWeatherId();
        var eorzeaHour = ReadEorzeaHour();
        var timeOfDay = configuration.ResolveTimeOfDay(eorzeaHour);

        CurrentEorzeaHour = eorzeaHour;

        var changed = force || weatherId != CurrentWeatherId || timeOfDay != CurrentTimeOfDay;
        if (!changed)
            return;

        CurrentWeatherId = weatherId;
        CurrentTimeOfDay = timeOfDay;
        log.Information($"[Presettingway] State: territory={CurrentTerritoryId}, weather={CurrentWeatherId}, timeOfDay={CurrentTimeOfDay} (eorzeaHour={eorzeaHour:F2})");
        StateChanged?.Invoke(CurrentTerritoryId, CurrentWeatherId, CurrentTimeOfDay);
    }

    private unsafe byte ReadWeatherId()
    {
        var mgr = WeatherManager.Instance();
        return mgr != null ? mgr->WeatherId : (byte)0;
    }

    private unsafe double ReadEorzeaHour()
    {
        var fw = GameFramework.Instance();
        if (fw == null)
            return CurrentEorzeaHour;

        var eorzeaSeconds = fw->ClientTime.EorzeaTime;
        return (eorzeaSeconds / 3600.0) % 24.0;
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update -= OnFrameworkUpdate;
    }
}

namespace Srt.EventCompanion.Core;

public enum GamePhase
{
    /// <summary>Launched; waiting for Assetto Corsa to start (Content Manager may still be loading or downloading).</summary>
    Starting,
    /// <summary>Assetto Corsa is running.</summary>
    InGame,
    /// <summary>The game was running and has closed, or never started. The session ends at <see cref="WatchStatus.AutoEndAt"/>.</summary>
    Away
}

/// <param name="AutoEndAt">When the session will be ended automatically; null while the game runs or the countdown is held.</param>
/// <param name="GameWasSeen">False when the game never started after the last launch.</param>
public sealed record WatchStatus(GamePhase Phase, DateTimeOffset? AutoEndAt, bool GameWasSeen, bool Held)
{
    public bool ShouldAutoEnd(DateTimeOffset now) => AutoEndAt is { } at && now >= at;
}

/// <summary>
/// Decides whether a session is still in use, from whether Assetto Corsa and Content Manager are running.
/// Pure logic (the caller passes in what is running), so it is unit-tested.
///
/// Assetto Corsa (acs.exe) runs exactly while the driver is in the game, so it is the signal. When it closes
/// the session is not ended at once: the driver may be rejoining. Instead a countdown starts, and the session
/// ends when it runs out unless the game comes back, staff select Rejoin, or staff hold the session open.
/// </summary>
public sealed class SessionWatcher
{
    /// <summary>How long a closed game waits for a rejoin before the session ends.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);
    /// <summary>How long after a launch the game may take to appear while Content Manager is open (loading, downloading a mod).</summary>
    public static readonly TimeSpan LauncherWait = TimeSpan.FromMinutes(10);
    /// <summary>How long after a launch the game may take to appear when Content Manager is not open either.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(45);

    private readonly TimeSpan _grace;
    private DateTimeOffset _launchedAt;
    private bool _gameSeen;
    private DateTimeOffset? _awaySince;
    private bool _held;

    /// <param name="gameSeen">True when picking up a session after the app restarted: no fresh launch to wait for.</param>
    public SessionWatcher(DateTimeOffset launchedAt, bool gameSeen = false, TimeSpan? grace = null)
    {
        _launchedAt = launchedAt;
        _gameSeen = gameSeen;
        _grace = grace ?? DefaultGrace;
    }

    /// <summary>The game was launched again (Rejoin). Start waiting for it afresh.</summary>
    public void Relaunched(DateTimeOffset now)
    {
        _launchedAt = now;
        _gameSeen = false;
        _awaySince = null;
        _held = false;
    }

    /// <summary>Staff want the session kept open; no countdown until the game has run and closed again.</summary>
    public void Hold() => _held = true;

    public WatchStatus Observe(bool gameRunning, bool launcherRunning, DateTimeOffset now)
    {
        if (gameRunning)
        {
            _gameSeen = true;
            _awaySince = null;
            _held = false;
            return new WatchStatus(GamePhase.InGame, null, true, false);
        }

        if (!_gameSeen)
        {
            var waited = now - _launchedAt;
            if (waited < StartWait || (launcherRunning && waited < LauncherWait))
                return new WatchStatus(GamePhase.Starting, null, false, _held);
        }

        _awaySince ??= now;
        return new WatchStatus(GamePhase.Away, _held ? null : _awaySince + _grace, _gameSeen, _held);
    }
}

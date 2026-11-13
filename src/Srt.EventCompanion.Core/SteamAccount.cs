using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Srt.EventCompanion.Core;

public enum SteamCheckStatus { Matches, NotSignedIn, Mismatch }

public sealed record SteamCheck(SteamCheckStatus Status, string RegisteredSteamId, string? SignedInSteamId)
{
    public bool IsOk => Status == SteamCheckStatus.Matches;

    /// <summary>Short status for the checklist row.</summary>
    public string Label => Status switch
    {
        SteamCheckStatus.Matches => $"✓ Matches rig (…{Tail(RegisteredSteamId)})",
        SteamCheckStatus.NotSignedIn => "Steam not signed in",
        _ => "Wrong Steam account"
    };

    /// <summary>What went wrong and what to do, for the person at the rig.</summary>
    public string Message => Status switch
    {
        SteamCheckStatus.Matches => string.Empty,
        SteamCheckStatus.NotSignedIn =>
            $"Steam is not running or not signed in. Open Steam and sign in to the account registered for this rig (SteamID64 {RegisteredSteamId}).",
        _ =>
            $"Steam is signed in to a different account ({SignedInSteamId}). This rig is registered for SteamID64 {RegisteredSteamId}. " +
            "Sign out of Steam and sign in to the registered account. If the registered account is wrong, contact the SRT Labs team."
    };

    public static SteamCheck Evaluate(string registeredSteamId, string? signedInSteamId) =>
        new(signedInSteamId is null ? SteamCheckStatus.NotSignedIn
            : string.Equals(signedInSteamId, registeredSteamId, StringComparison.Ordinal) ? SteamCheckStatus.Matches
            : SteamCheckStatus.Mismatch,
            registeredSteamId, signedInSteamId);

    private static string Tail(string steamId) => steamId.Length > 4 ? steamId[^4..] : steamId;
}

/// <summary>The Steam account signed in on this PC right now (not the one that last ran Assetto Corsa).</summary>
public static class SteamAccount
{
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>Steam stores the signed-in account as a 32-bit account ID; SteamID64 = base + account ID. 0 means nobody is signed in.</summary>
    public static string? FromAccountId(long accountId) =>
        accountId is > 0 and <= uint.MaxValue ? (SteamId64Base + (ulong)accountId).ToString() : null;

    [SupportedOSPlatform("windows")]
    public static string? SignedInSteamId()
    {
        // Steam keeps the last value briefly after a crash, so also require the Steam client to be running.
        if (Process.GetProcessesByName("steam").Length == 0) return null;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        var value = key?.GetValue("ActiveUser");
        return value switch
        {
            int accountId => FromAccountId((uint)accountId),
            long accountId => FromAccountId(accountId),
            _ => null
        };
    }

    [SupportedOSPlatform("windows")]
    public static SteamCheck Check(string registeredSteamId) => SteamCheck.Evaluate(registeredSteamId, SignedInSteamId());
}

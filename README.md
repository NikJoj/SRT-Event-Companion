# SRT Event Companion

Windows companion for Kerala Race Wars rigs running the original Assetto Corsa through Content Manager.

## Milestone 1: rig readiness

This repository currently proves the local integration boundary without starting Assetto Corsa or publishing anything to the central API:

- detects an Assetto Corsa installation and Documents profile;
- reads the active `race.ini` profile;
- previews a driver-name update; and
- applies the update atomically with a timestamped backup.

The implementation updates both `[CAR_0] DRIVER_NAME` and `[REMOTE] NAME`, which are both present in the detected rig profile. It does not assume Content Manager's URL/command-line contract or an external telemetry protocol. Those must be validated on a representative multiplayer rig before milestone 2 uses them.

## Repository layout

- `src/Srt.EventCompanion.Core` — safe local AC profile integration.
- `tests/Srt.EventCompanion.Core.Tests` — dependency-free test runner.
- `docs` — discovery notes and the delivery plan.

## Prerequisite

Install the .NET 10 SDK (the runtime alone cannot build projects), then run:

```powershell
dotnet run --project tests/Srt.EventCompanion.Core.Tests/Srt.EventCompanion.Core.Tests.csproj
```

To inspect the configured rig after building:

```powershell
dotnet run --project src/Srt.EventCompanion.Rig/Srt.EventCompanion.Rig.csproj -- inspect
```

To preview an online join using the verified Content Manager protocol:

```powershell
dotnet run --project src/Srt.EventCompanion.Rig/Srt.EventCompanion.Rig.csproj -- join --ip 15.235.182.31 --http-port 8027
```

The command prints the join URI and makes no change. Add `--launch` only when ready to hand the URI to Content Manager:

```powershell
dotnet run --project src/Srt.EventCompanion.Rig/Srt.EventCompanion.Rig.csproj -- join --ip 15.235.182.31 --http-port 8027 --launch
```

Content Manager will handle any server-password prompt. The companion intentionally never accepts passwords as arguments or embeds them in join links.

## Windows test UI

Run the WPF test UI with:

```powershell
dotnet run --project src/Srt.EventCompanion.Desktop/Srt.EventCompanion.Desktop.csproj
```

Use **Preview Join** to inspect the profile update and Content Manager URI without changing the rig. **Launch Content Manager** creates an AC profile backup, applies the entered display name, then invokes the verified join URI.

## Safety

Never place server passwords, connection strings, or account secrets in this repository. Rig configuration will be provisioned later by the SRT backend.

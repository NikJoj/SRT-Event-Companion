# SRT Event Companion — implementation status

Updated: 13 November 2026

## Purpose

SRT Event Companion is a Windows rig-side application for original Assetto Corsa and Content Manager. It will manage practice and race sessions, attribute every lap/attempt automatically, and connect with the existing SRT Azure Functions and Neon PostgreSQL system.

## Delivered

### Rig readiness

- Detects the standard Steam Assetto Corsa installation and `Documents/Assetto Corsa/cfg/race.ini` profile.
- Reads the offline and multiplayer driver-name values.
- Previews a name change without modifying the rig.
- When a launch is confirmed, backs up and updates both AC driver-name fields plus Content Manager's authoritative `PlayerName` setting.

### Content Manager launching

- Confirmed the rig can join the provided test server through the `acmanager://race/online/join` protocol.
- Builds a typed, validated join URI from an IPv4 address and HTTP port.
- Opens the URI through Windows so Content Manager owns the handoff to Assetto Corsa.
- Does not accept, store, log, or place passwords in URLs.

### Session workflow

- Supports exactly two stages: `Practice` and `Race`.
- Validates permanent SRT usernames and display names before a session can be prepared.
- Enforces a legal local lifecycle: idle → prepared → launch requested → running → completed/aborted.
- Has no official-attempt approval stage.
- Persists the active local session and restores the rig's previous AC and Content Manager identities with **End Session & Restore**, once both applications are closed.

### Windows user interface

- Added a WPF desktop window for local test use.
- Allows staff to select Practice or Race, enter an internal SRT username and the separate server-visible in-game display name, enter the server IP/HTTP port, preview the AC profile change and join URI, then launch Content Manager.
- The launch button clearly states its two effects: updates the local AC profile with a backup, then opens Content Manager.

### Shared backend foundation

- Added a protected Kerala Race Wars read endpoint to the existing SRT Azure Functions backend: `GET /api/kerala-race-wars`.
- Added idempotent Neon schema creation for centres, rigs, events, practice/race sessions, and automatic attempt records.
- Enforces one active session per rig and permits only `practice` or `race` stages; no official-attempt approval model exists.
- Reuses the existing Discord admin session protection and documents the endpoint in the SRT OpenAPI contract.

### Backend sync, live event config and driver identity (13 Nov 2026)

- **Live event from SRT.** Admins create events in the dashboard (Kerala Race Wars → Event & server) with server IP, HTTP port and password, and make one live for all centres. Before every launch the rig fetches it from `GET /api/kerala-race-wars-config`; nothing is hard-coded. The password is never put in the join link: it is shown on the session screen with a Copy button.
- **Driver identity.** Staff search a Discord or SRT username (exact match only). Existing SRT drivers get an SRT username (Discord username pre-filled, editable); their Discord username is kept unchanged on the profile. New drivers create an SRT username, which cannot match any existing SRT or Discord username. Stored as `driver_profiles.srt_username` so the website can use it later.
- **Session sync.** Start-session, laps and end-session are written to a local outbox (`%LOCALAPPDATA%\SrtEventCompanion\outbox`) and sent in order; while offline they retry automatically (15 s doubling to 5 min). The backend treats repeats as success, accepts late laps that were driven before End Session and rejects any captured after it, and closes a crashed rig's stale session when the next one starts.
- **End Session & Restore** now closes Assetto Corsa and Content Manager itself, sends all queued data, then restores the rig name from the launch backup — or, if a backup is missing, the rig name from SRT.



- .NET 10 SDK installed successfully.
- Current solution build completes with zero warnings and errors.
- 18 automated core tests pass (14 backend tests in the SRT repo): profile reads/writes, backup safety, installation detection, session gate, both stages, lifecycle enforcement, and join URI validation.
- Manual rig test confirmed successful Content Manager/Assetto Corsa connection through the supplied invite URL.

## Not implemented yet

- Centrally provisioned centre, rig, event, and secure device configuration.
- Driver registration and permanent SRT username persistence in Neon.
- Local session persistence and offline outbox.
- Automatic lap/attempt ingestion. The authoritative timing source must still be chosen and tested; local `.tc` telemetry artifacts are not assumed to be a live, supported timing interface.
- Authenticated rig write endpoints for configuration, session creation, and automatic attempt ingestion.
- The existing SRT website admin dashboard section for displaying the new cross-centre data.
- Multi-rig, cross-centre, offline-retry, and duplicate-lap testing.

## Safe UI test

Build and run the desktop app:

```powershell
dotnet run --project src/Srt.EventCompanion.Desktop/Srt.EventCompanion.Desktop.csproj
```

For a non-launching test, enter a display name and select **Preview Join**. To perform the handoff, select **Launch Content Manager**; Content Manager handles any password prompt.

# Rig and SRT discovery

## Confirmed on the inspected machine

- Original Assetto Corsa is installed under the standard Steam location and exposes `AssettoCorsa.exe`.
- The user profile is `Documents/Assetto Corsa` and contains `cfg/race.ini`.
- `race.ini` currently includes `[CAR_0] DRIVER_NAME` and `[REMOTE] NAME`; both are used for the active local/multiplayer profile and are updated together by milestone 1.
- The AC profile contains server address/port fields, but these are treated as sensitive operational configuration and are never committed or logged by this project.
- Local `ctelemetry` data and telemetry-save log entries are present. This confirms that the installed game can record local telemetry files, not that they are a supported real-time event feed.
- Content Manager is installed/used on this rig, but its external launch URL/CLI contract has not been validated. The public product page describes it as an alternative launcher but does not document a stable automation interface.
- Content Manager stores historical session snapshots locally. They can include complete connection configuration, so this project must neither read them as an input nor log their raw contents.
- A manual invite test successfully connected this rig through `acmanager://race/online/join` with the server IP and HTTP port parameters. The companion now creates this URI but deliberately omits passwords: they are not URL-safe and must be provisioned separately by the future authenticated rig configuration flow.

## Consequences

Milestone 1 uses the file-backed profile surface only. It does not:

- launch Content Manager or AC;
- parse `.tc` telemetry files;
- assume AC publishes a UDP stream; or
- record or send any laps.

Milestone 2 adds a practice/race session state machine. It accepts no other session stage and blocks preparation until Content Manager integration is explicitly marked verified for the rig. The verified join URI is now implemented; a future session coordinator will call it only after central configuration and local driver-profile updates succeed.

## Required rig validation before milestone 2

1. Capture an actual Content Manager online join invocation on a disposable test profile and identify a stable, documented mechanism.
2. Run a short multiplayer practice and race session, then compare server results, `race.ini`, logs, and telemetry artifacts.
3. Select an authoritative lap source (prefer server-side session results or a controlled timing plugin) and document its unique lap identifiers and late-result behavior.
4. Verify name propagation on the multiplayer server after profile updates and before launch.

## Existing SRT website integration

The existing SRT web repository is a static Azure Static Web Apps frontend with JavaScript and Azure Functions, not a Next.js application. Its API uses `@neondatabase/serverless` and an existing Discord-admin session check. Companion API endpoints and a Kerala Race Wars admin section should be added to that repository in milestone 3, after the rig data contract is proven.

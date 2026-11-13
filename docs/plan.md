# Delivery plan

## Milestone 1 — rig readiness (implemented)

Deliver and test safe Assetto Corsa profile discovery and driver-name updates with backups. No session, lap, API, or Content Manager launch behaviour is introduced.

Acceptance checks:

- the service detects standard Steam + Documents locations;
- it reads `[CAR_0] DRIVER_NAME` and `[REMOTE] NAME`;
- previewing a change is read-only;
- applying a change creates a backup and preserves unrelated values.

## Milestone 2 — controlled session shell (in progress)

An explicit practice/race state machine and launch gate are implemented. The gate refuses every session before Content Manager integration is verified for that rig. A manual on-rig test verified the Content Manager join protocol, so the adapter now creates and opens the verified `acmanager://` URI without embedding passwords.

## Milestone 3 — central records and SRT admin view

Extend the existing Azure Functions/Neon database with centres, rigs, drivers, sessions, attempts, and idempotent lap ingestion. Add a Kerala Race Wars admin section protected by the current SRT admin authentication.

Every captured attempt is persisted automatically. Practice and race are session stages; no approval or official-attempt workflow exists.

## Milestone 4 — timing and reliability

Ingest the validated authoritative timing feed, reconcile late results, persist an encrypted local outbox, and test duplicate, offline, restart, and multi-rig scenarios.

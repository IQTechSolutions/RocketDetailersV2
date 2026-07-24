# Changelog

All notable changes to this project are documented in this file.

Format: `## [MAJOR.MINOR.PATCH.MICRO] - YYYY-MM-DD` with Added / Changed / Fixed / Removed sections.

## [0.0.0.4] - 2026-07-24

### Fixed

- Dunning delivery-verification (F2) could never run in production. The append-only guard treated every `DunningAttempt` update as forbidden, so the delivery-verification job threw on each run, and GHL dunning triggers stamped the attempt, crashed before persisting, and re-fired the workflow to the real contact on every retry. Attempts stay delete-protected, but their verification trail (`TriggeredAt` / `VerifiedAt` / `FailureReason`) can now be written, so delivery verification and dunning triggers work as designed.
- Outbox retry backoff is now actually enforced. A transient gateway failure scheduled a backoff (`NextAttemptAt`) that the claim query ignored, so a failed action was re-claimable on the immediate next pass and burned all its attempts almost instantly instead of backing off. The claim query now honors `NextAttemptAt`.

### Changed

- Added DB-backed test coverage from the /ship coverage audit (F2 delivery verification, enforced retry backoff, kill-switch engage/release, action stager, Stripe webhook variants) and recorded the remaining M2 coverage debt and append-only hardening items in TODOS.md.

## [0.0.0.3] - 2026-07-24

### Added

- Builds are now stamped with the release version: every assembly's product, file, and assembly version comes from the repo-root VERSION file, so a deployed binary can always be traced back to the exact release that produced it.
- Version safety guard: the build fails with a clear error if the VERSION file is missing (e.g. an incomplete checkout or container copy) or doesn't match the MAJOR.MINOR.PATCH.MICRO format, instead of silently shipping a wrong version.

## [0.0.0.2] - 2026-07-24

### Fixed

- Resolved a moderate-severity security advisory (GHSA-pgww-w46g-26qg): the test project previously resolved AngleSharp 1.4.0 through bunit, which has a known mutated-XSS parsing flaw. A direct pin lifts it to the patched 1.5.2, so builds are clean of the NU1902 audit warning.

## [0.0.0.1] - 2026-07-24

### Added

- M1 — event log and ledger: solution scaffold with hardened schema and seed importer; vendor gateways, sync jobs, projections, and idempotent ledger ingestion; EligibilityPolicy pure function with golden tests; MudBlazor cockpit (clients, reconciliation work queue) with Hangfire wiring and shadow verdicts surfaced in the queue.
- M2 — enforcement wedge: Stripe webhook receiver (signature verify, recoverable inbox, idempotent processing); outbox dispatcher, staging, approval CAS, safety profile, and delivery-verify jobs; mapping-fix wizard (evidence, blast-radius, verify, Shadow→Assist promotion) and kill-switch UI.

### Fixed

- Resolved a high-severity security advisory (GHSA-5crp-9r3c-p9vr): the app previously shipped Newtonsoft.Json 11.0.1 pulled in through Hangfire. All projects that consume RD.Infrastructure (RD.Web, RD.Tools.Import) now resolve Newtonsoft.Json 13.0.4 via a single pin there, so builds are clean of the NU1903 audit warning and future consumers of RD.Infrastructure inherit the safe version automatically.

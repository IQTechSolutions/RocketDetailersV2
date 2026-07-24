# Changelog

Notable changes to RocketDetailersV2. Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

## Unreleased

### M2 — enforcement wedge
- Stripe webhook receiver (signature verify, recoverable inbox, idempotent processing)
- Outbox dispatcher, staging, approval CAS, safety profile, delivery-verify jobs
- Mapping-fix wizard (evidence, blast-radius, verify, Shadow→Assist promotion) and kill-switch UI

### M1 — event log and ledger
- Solution scaffold, hardened schema, seed importer
- Vendor gateways, sync jobs, projections, idempotent ledger ingestion
- EligibilityPolicy pure function with golden tests; shadow verdicts surfaced in the cockpit queue
- MudBlazor cockpit: clients and reconciliation work queue; Hangfire wiring and PolicyEvaluationJob

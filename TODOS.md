# TODOS

Deferred work with context. Nothing here is forgotten — it is deliberately sequenced.

## Deferred by CEO review (2026-07-24)

### Automated ad-creation pipeline (v2 flagship)
The other half of the business: campaigns build themselves from onboarding data, with one human review before launch. Deferred until the control plane (M0–M3.5) has earned operational trust.

- Shape: campaign-template engine over the Meta Marketing API — create campaign/adset/ad from a package blueprint + client variables (city callouts → 11labs snippet, package → offer creative, radius/zip from onboarding); asset library for human hooks and callouts; mandatory human review gate before activation.
- Effort: L (human ~3-4 wks / CC ~1-1.5 wks). Risk: High (Meta ad-creation APIs, ad review policies, creative assets, client-revenue-critical).
- Prerequisites: onboarding pipeline (M4+) supplying structured inputs; stable Meta System User token; packages admin.
- Origin: owner's brief — "Add then gets manually created by virtual assistant, would like to automate this as well maybe v2".

### Stripe payout/fee/settlement reconciliation (from outside-voice review)
The v1 ledger is receivables-vs-spend (charges in, Meta spend out, refunds/disputes reversed). True cash accounting — Stripe fees, payout timing, settlement failures, taxes — is bookkeeping-grade work deferred until the owner asks for it.
- Effort: M (human ~1 wk / CC ~2 days). Priority: P3. Depends on: M1 ledger in production.

## Test coverage debt — M2 (from /ship coverage audit, 2026-07-24)

The M2 spine audited at ~56% path coverage. This session added 13 tests (dispatcher
retry ladder + enforced backoff, kill-switch engage/release, action stager, F2
delivery verification, Stripe webhook variants) and fixed two production bugs found
during the audit (DunningAttempt immutability blocking F2; unenforced retry backoff).
Remaining gaps, in priority order:

- **P1 — `RD.Web/Services/MappingWizardService.cs` DB-write paths are untested.** Highest-risk untested write is `AddOrReplaceLink` (invalidate prior link + invalidate verification + demote client to Shadow, in one transaction); also `VerifyMapping` refusals/supersede, `PromoteToAssist` DB path, `ResolveDuplicateStripe`. Only the extracted pure helpers in `MappingLogic.cs` are covered. (A test agent was mid-write on these when the session hit a model limit.)
- **P2 — dispatcher execution arms + sequence-group claiming.** `ExecuteResumeAsync`, `WriteGhlField`, `TriggerGhlWorkflow` happy/error arms, and ordered `SequenceGroup` claiming, are not directly exercised.
- **P2 — gateway error paths.** `GhlGateway.TriggerWorkflowAsync` non-2xx handling; `MetaAdsGateway` resume + `GetCampaign` 404→null + non-convergence result.
- **P3 — UI/E2E flows** [→E2E]: mapping wizard journey (select→suggest→link→verify→promote), kill-switch card engage/release, ActionQueue approve/dismiss snackbars incl. the AlreadyResolved race message.

## Hardening from /ship review (2026-07-24)

- **P2 — append-only enforcement is entity-granular, not column-granular.** `AppendOnlyInterceptor` blocks *any* modification of a `StrictlyImmutable` type, but append-only types that need lifecycle columns (`DunningAttempt`, `WebhookInboxItem`) were moved to deletes-only so their status columns can be written. That leaves their financial/identity columns (`DunningAttempt.Step`, `DueAt`, `DunningCaseId`) mutable in principle — no active corruption path today, but a future edit could silently break the audit trail. Close both with a per-type mutable-column allowlist in the interceptor.
- **P3 — outbox lease recovery.** A dispatcher that crashes between claim and the try/catch leaves a row `Status='Leased'`; the claim query only matches `Status='Approved'` and there is no sweeper to reclaim an expired `LeaseUntil`. Pre-existing; add a recovery sweep. (`OutboxDispatcher.ClaimBatchAsync`)
- **P4 — index coverage.** The outbox claim query now filters `NextAttemptAt`, which isn't in `IX_OutboxActions_Status_NotBefore_LeaseUntil`; it's a residual predicate. Immaterial at single-tenant M2 volumes; add `NextAttemptAt` to the index if outbox depth grows.

## Design-doc open questions awaiting owner answers (M0 Discovery Week)
See "Open Questions" in the design doc (`~/.gstack/projects/IQTechSolutions-RocketDetailersV2/ivanr-main-design-20260724-000239.md`): master-account Meta payment method post-April-2026 (OQ1), Make scenario inventory (OQ2), trial semantics (OQ4), dunning + payment-link mechanics (OQ6), one-off vs subscription on trial close (OQ7), trial expiry data location (OQ8), Stripe key scopes (OQ9), GHL auth model (OQ10), billing model fixed vs metered (OQ11), ledger backfill depth (OQ12).

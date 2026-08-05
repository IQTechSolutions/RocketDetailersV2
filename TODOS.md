# TODOS

Deferred work with context. Nothing here is forgotten — it is deliberately sequenced.

## Ads Automation

### Automated ad-creation pipeline (v2 flagship)

**What:** Campaign-template engine over the Meta Marketing API — campaigns build themselves from onboarding data, with a mandatory human review gate before launch.

**Why:** The other half of the business: removes the manual virtual-assistant step in ad creation. Origin: owner's brief — "Add then gets manually created by virtual assistant, would like to automate this as well maybe v2".

**Context:** Deferred by CEO review (2026-07-24) until the control plane (M0–M3.5) has earned operational trust. Shape: create campaign/adset/ad from a package blueprint + client variables (city callouts → 11labs snippet, package → offer creative, radius/zip from onboarding); asset library for human hooks and callouts; mandatory human review gate before activation. Risk: High (Meta ad-creation APIs, ad review policies, creative assets, client-revenue-critical).

**Effort:** L (human ~3-4 wks / CC ~1-1.5 wks)
**Priority:** P3
**Depends on:** Onboarding pipeline (M4+) supplying structured inputs; stable Meta System User token; packages admin

## Ledger & Billing

### Stripe payout/fee/settlement reconciliation

**What:** True cash accounting — Stripe fees, payout timing, settlement failures, taxes.

**Why:** The v1 ledger is receivables-vs-spend (charges in, Meta spend out, refunds/disputes reversed). Bookkeeping-grade reconciliation is deliberately out of v1 scope.

**Context:** From outside-voice review (2026-07-24). Deferred until the owner asks for it.

**Effort:** M (human ~1 wk / CC ~2 days)
**Priority:** P3
**Depends on:** M1 ledger in production

### Master-account variable-billing wedge (Convert→Bill→Close follow-on)

**What:** Automate trial→subscriber conversion billing for **master-account** (Meta-flagged) clients, whose subscription covers *variable* ad spend + service — via metered/usage Stripe billing (metered price or per-cycle invoice items computed from the ad-spend ledger).

**Why:** The own-account Convert→Bill→Close wedge ships first with a flat service-fee Stripe Price (a fixed price can't represent variable ad-spend billing). Master-account is the exception path, not the default, so it stays manual until the own-account machine is proven. Ties into Step-3 "payments cover ad spend" enforcement, which is master-account-only.

**Context:** From /plan-eng-review outside voice (2026-07-25). The own-account wedge proves the whole Convert→Bill→Close machine (button, ConvertIntent, draft, Stripe idempotency-key, webhook→intent correlation via `metadata.convert_intent_id`, `closed` write, GhlWriteMarker loop-safety, cancel-on-reverse). Master adds only the variable-billing computation on top. Keep the `ConvertIntent.AccountType` branch point in the own-account build so master is a drop-in, not a rewrite. See design doc `~/.gstack/projects/IQTechSolutions-RocketDetailersV2/ivanr-feat-identity-admin-console-design-20260725-192624.md` (Outside-Voice Hardening section).

**Effort:** M (human ~1-2 wks / CC ~½ day)
**Priority:** P2
**Depends on:** Own-account Convert→Bill→Close wedge shipped; ad-spend ledger (M1) in production

## Testing

### M2 test coverage debt (from /ship coverage audit, 2026-07-24)

**What:** Close the remaining untested paths in the M2 enforcement spine.

**Why:** Untested enforcement paths touch client money and messaging; regressions there are silent and expensive.

**Context:** The /ship audit measured ~56% path coverage. This work added 13 tests (dispatcher retry ladder + enforced backoff, kill-switch engage/release, action stager, F2 delivery verification, Stripe webhook variants) and fixed two production bugs found during the audit (DunningAttempt immutability blocking F2; unenforced retry backoff). Remaining gaps:
- **P1** — `RD.Web/Services/MappingWizardService.cs` DB-write paths untested. Highest-risk is `AddOrReplaceLink` (invalidate prior link + invalidate verification + demote client to Shadow, in one transaction); also `VerifyMapping` refusals/supersede, `PromoteToAssist` DB path, `ResolveDuplicateStripe`. Only the pure helpers in `MappingLogic.cs` are covered.
- **P2** — dispatcher execution arms + sequence-group claiming: `ExecuteResumeAsync`, `WriteGhlField`, `TriggerGhlWorkflow` happy/error arms, ordered `SequenceGroup` claiming.
- **P2** — gateway error paths: `GhlGateway.TriggerWorkflowAsync` non-2xx; `MetaAdsGateway` resume + `GetCampaign` 404→null + non-convergence result.
- **P3** — UI/E2E flows: mapping wizard journey (select→suggest→link→verify→promote), kill-switch card engage/release, ActionQueue approve/dismiss snackbars incl. the AlreadyResolved race message.

**Priority:** P1 (highest remaining item)
**Depends on:** —

### Append-only enforcement hardening (from /ship review, 2026-07-24)

**What:** Harden `AppendOnlyInterceptor` and outbox lease recovery.

**Why:** Entity-granular immutability leaves lifecycle-bearing evidence rows partially unprotected, and a lease gap can strand outbox actions.

**Context:**
- **P2** — append-only enforcement is entity-granular, not column-granular. Append-only types that need lifecycle columns (`DunningAttempt`, `WebhookInboxItem`) are deletes-only, so their financial/identity columns (`DunningAttempt.Step`, `DueAt`, `DunningCaseId`) are mutable in principle — no active corruption path today, but a future edit could silently break the audit trail. Close with a per-type mutable-column allowlist in the interceptor.
- **P3** — outbox lease recovery: a dispatcher that crashes between claim and the try/catch leaves a row `Status='Leased'`; the claim query only matches `Status='Approved'` and there is no sweeper to reclaim an expired `LeaseUntil`. Add a recovery sweep. (`OutboxDispatcher.ClaimBatchAsync`)
- **P4** — index coverage: the outbox claim query now filters `NextAttemptAt`, which isn't in `IX_OutboxActions_Status_NotBefore_LeaseUntil`; add it if outbox depth grows.

**Priority:** P2
**Depends on:** —

## Discovery (M0)

### Design-doc open questions awaiting owner answers

**What:** Resolve the design doc's open questions during M0 Discovery Week.

**Why:** These answers gate trial, billing, and enforcement implementation choices.

**Context:** See "Open Questions" in the design doc (`~/.gstack/projects/IQTechSolutions-RocketDetailersV2/ivanr-main-design-20260724-000239.md`): master-account Meta payment method post-April-2026 (OQ1), Make scenario inventory (OQ2), trial semantics (OQ4), dunning + payment-link mechanics (OQ6), one-off vs subscription on trial close (OQ7), trial expiry data location (OQ8), Stripe key scopes (OQ9), GHL auth model (OQ10), billing model fixed vs metered (OQ11), ledger backfill depth (OQ12).

**Effort:** S
**Priority:** P1
**Depends on:** Owner availability

## Cockpit

### Linked-client count can exceed the total (merged-client filter asymmetry)

**What:** Filter `clientsLinked` on `MergedIntoClientId == null`, the way `totalClients`, `campaignsLive`, and the ledger roll-up already are.

**Why:** `CockpitStateService.LoadAsync` counts `totalClients` excluding merged duplicates but counts `clientsLinked` straight off `IdentityLinks` with no join back to `Clients`. A merged-away duplicate that still carries an active Stripe subscription link is counted as linked but not as a total, so the first-run card can read "12 of 10 clients linked" and `_percent` can push the progress bar past 100%. Live-relevant: the deployment uses the client-merge feature.

**Context:** Found by cross-model adversarial review during the 0.0.1.0 ship (2026-08-05). Pre-existing, not introduced by that change. Needs a regression test proving a merged client with an active subscription link does not inflate the linked count. (`src/RD.Web/Services/CockpitStateService.cs`)

**Effort:** S (human ~1h / CC ~15min)
**Priority:** P2
**Depends on:** —

### Empty-roster guidance disappears after the first sync

**What:** Keep the "import your client roster" step reachable on a deployment that has zero clients, regardless of sync history.

**Why:** `CockpitRules.Compute` decides `CockpitState.FirstRun` solely on `CompletedSyncRuns.Count == 0`, and `Cockpit.razor` only renders `CockpitFirstRun` in that state. The scheduled Stripe/Meta jobs complete fine against an empty roster, so the first sync retires the onboarding card permanently — a fresh install that sits one sync interval before anyone logs in never sees the import step, and gets a KPI row or Stale banner over an empty database instead.

**Context:** Found by cross-model adversarial review during the 0.0.1.0 ship (2026-08-05), which added the empty-state import CTA to that card. Either make the state ladder roster-aware or surface an empty-roster banner in the non-FirstRun branch. Note the render tests mount `CockpitFirstRun` directly and cannot catch state-routing regressions — cover this at the `CockpitRules` level. (`src/RD.Web/Services/CockpitRules.cs`, `src/RD.Web/Components/Pages/Cockpit.razor`)

**Effort:** S (human ~2h / CC ~20min)
**Priority:** P3
**Depends on:** —

## Completed

_(none yet)_

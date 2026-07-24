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

## Design-doc open questions awaiting owner answers (M0 Discovery Week)
See "Open Questions" in the design doc (`~/.gstack/projects/IQTechSolutions-RocketDetailersV2/ivanr-main-design-20260724-000239.md`): master-account Meta payment method post-April-2026 (OQ1), Make scenario inventory (OQ2), trial semantics (OQ4), dunning + payment-link mechanics (OQ6), one-off vs subscription on trial close (OQ7), trial expiry data location (OQ8), Stripe key scopes (OQ9), GHL auth model (OQ10), billing model fixed vs metered (OQ11), ledger backfill depth (OQ12).

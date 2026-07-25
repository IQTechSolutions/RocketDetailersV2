# Spike: GHL `close` tag write — results and remaining unknowns

**Status:** PARTIALLY ANSWERED (2026-07-25). The write mechanism is confirmed; two questions remain, both requiring higher GHL permissions and/or a controlled live test.
**Location:** Detail Launch (`TMLPbGlvrW0AjGkAlY6d`).

> **Correction:** the tag is **`close`**, not `closed`. There is no `closed` tag in this location — the only tag matching `clos` is exactly `close`. All references below use `close`. (Our internal `ConvertIntentState.Closed` enum is a separate namespace and is unaffected.)

---

## Findings (2026-07-25)

1. **Tag = `close`.** Confirmed the only `clos*` tag is `close`.
2. **`close` fires (at least) two published workflows:**
   - **"Closed Deal Webhook Trigger"** — trigger `Contact Tag Added: close`. Sole action: a POST webhook "Send Custom Webhook to RD" carrying contact id, name, email, phone. **46 enrollments, executed successfully today** (so `close` is live and active).
   - **"Tag Closed -> Send Webhook to Zapier"** — **127 enrollments**, but the spike login gets **"insufficient permissions"** to open it. Almost certainly the real downstream onboarding chain (sub-account + welcome SMS via Zapier), but unconfirmed.
3. **Write mechanism confirmed:** `POST /contacts/:contactId/tags`, body `{"tags":["close"]}`. (See the API-version note below.)
4. **Duplicate-add behavior is NOT guaranteed by GHL's docs** — so we cannot assume re-adding an existing `close` is a no-op. **→ read-before-write is mandatory, not optional.**
5. **The double-POST idempotency test was NOT run**, deliberately: the test token is masked, minting a new key needs sign-off, and the POST has real side effects (fires the live "Send Custom Webhook to RD" and could trigger SMS / account creation). Correct call — this must be tested on a throwaway contact with eyes open, not casually.

### ⚠️ Two issues worth flagging (independent of our work)

- **The "Send Custom Webhook to RD" URL ends in `/webhook-trigger/undefined`.** `undefined` is a leaked template variable — this webhook is posting to a dead/garbage URL. If "RD" was meant to be this app (or any real endpoint), that GHL→RD notification has been silently broken. It does not block our work (we *write* `close`; we don't depend on that outbound webhook), but it's a real misconfiguration in the live setup. Worth a separate investigation: what was it supposed to hit, and is anything relying on it?
- **The 127-enrollment "Tag Closed → Zapier" workflow is invisible to the spike login.** Confirming Q1 (does an API tag-add fire the real onboarding chain?) needs someone with permission to open it — to verify its trigger really is `close` and to see what it does.

---

## API-version note

The user's notes cite header `Version: v3`. The app currently pins **`Version: 2021-07-28`** (`GhlGateway.ApiVersion`) against `https://services.leadconnectorhq.com` — the LeadConnector v2 API, which is where `POST /contacts/:contactId/tags` lives. Use the app's existing pin (`2021-07-28`) unless a live call proves the tags endpoint needs a different version. Confirm this on the same controlled test as Q1/Q2.

---

## Locked design conclusions (safe to build against now)

1. **Tag string is `close`** — hardcode it in the write allowlist.
2. **No `GhlWriteMarker` / inbound-echo layer.** The app has no inbound GHL webhook; the outbound "Send Custom Webhook to RD" goes to a dead URL, not into any reactor in this app. There is no echo loop to guard against. (Revisit only if the team separately decides to ingest inbound GHL webhooks.)
3. **Read-before-write is MANDATORY** (GHL doesn't guarantee duplicate-add is a no-op): before writing, GET the contact's tags and skip the add if `close` is already present, so an outbox retry can't double-fire the chain.
4. **Write via the existing GHL v2 API** (`services.leadconnectorhq.com`, `Version: 2021-07-28`), a new `GhlGateway.AddContactTagAsync(contactId, ["close"])`.

## Remaining unknowns (need higher permissions / a controlled test)

- **Q1:** Does an *API* `close` add fire the "Tag Closed → Zapier" chain (the real onboarding)? — needs the workflow opened, or a throwaway-contact test observed end to end.
- **Q2:** Is re-adding an existing `close` a no-op at the trigger level? — the controlled double-POST test (throwaway contact), watching whether the chain fires twice.

Both tests should run on a **disposable test contact**, accepting that the first add will fire the live chain (webhook + possible SMS/account creation). Mint a scoped API key first (with sign-off).

---

## Build plan once Q1/Q2 are confirmed

1. `GhlGateway.AddContactTagAsync(contactId, tag)` → `POST /contacts/{id}/tags` `{"tags":["close"]}`, plus a `GetContactTagsAsync` for the read-before-write check. Hardcoded allowlist: `close` only.
2. A `WriteGhlTag` outbox action, enqueued inside the first-payment promotion (`StripeWebhookIngestor.PromoteConversionAsync`), with the read-before-write guard. Rides the Shadow→Assist→Auto ladder — **Shadow first** (records "would add `close` to contact X", no live call) so targeting is validated against real conversions before anything fires.
3. Chain-observability follow-up (outside-voice #5): after the write, verify the sub-account contact appeared, so "SQL/Stripe/GHL stay consistent" is checkable, not asserted.

**Buildable now without Q1/Q2:** the Shadow-only version (step 2 in Shadow) fires nothing and lets us validate contact resolution + read-before-write against live conversions. The Assist/Auto flip waits on Q1/Q2.

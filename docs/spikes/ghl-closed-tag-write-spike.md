# Spike: GHL `closed` tag write — what to check before building the last piece of B

**Status:** open — blocks the final piece of Convert→Bill→Close (the `closed` GHL tag write on first payment).
**Owner:** needs someone with GHL builder + API access to the Detail Launch location (`TMLPbGlvrW0AjGkAlY6d`).
**Time:** ~30–60 min in the GHL builder + one API test.

---

## TL;DR — read this first

The design assumed we'd need a `GhlWriteMarker` "loop-safety" layer to stop the app's own tag write from echoing back through an inbound GHL webhook and being re-processed. **A code check says that echo loop does not exist in this app today:**

- The app has exactly **one** inbound webhook endpoint: `POST /webhooks/stripe`. There is **no inbound GHL webhook handler** anywhere.
- GHL data flows in by **polling only** (`GhlMessageSyncJob` sweeps conversations on a schedule). The app does not react to GHL tag changes at all.

So writing `closed` cannot echo back into a reactor that re-fires — because there is no reactor and no inbound GHL webhook. **The `GhlWriteMarker` loop-safety machinery is almost certainly unnecessary.** Unless this spike decides to *add* an inbound GHL webhook (for some other reason), the last piece of B collapses to: *add one tag-write gateway method + one outbox action, idempotent by read-before-write.*

This spike exists to confirm that, and to nail the three things the tag write actually depends on.

---

## What the `closed` write is for

On the first-payment webhook, the app already promotes the conversion to `Paid` and the trial to `Promoted` (shipped). The remaining step is to write the `closed` tag on the client's GHL contact, which **detonates the trusted downstream chain** the operator relies on today: the sub-account contact gets created, the welcome SMS fires, onboarding proceeds. Today a human does this by hand ("the manager signs into GHL and changes the tag to closed"). This piece automates that one write.

**How the write would work (already-present plumbing):**
- The client's GHL contact id is stored as an `IdentityLink` (`System=Ghl, Kind=Contact`) — resolvable per client.
- `GhlGateway` today has `SetContactFieldAsync`, `TriggerWorkflowAsync`, `CreateContactAsync` — **but no add-tag method.** A new `AddContactTagAsync(contactId, tag)` (POST `/contacts/{contactId}/tags`) is needed.
- It rides the existing outbox (`OutboxActionType` already has `WriteGhlField`/`TriggerGhlWorkflow`; add `WriteGhlTag` or reuse), so it's audited, retried, and Assist/Auto-gated like every other external write.

---

## The questions to answer (reframed after the code check)

### Q1 (primary) — Does writing `closed` via the API actually fire the downstream chain?
GHL workflow triggers of type "Contact Tag" usually fire whether the tag was added by a human in the UI *or* by an API call — but confirm it, because the whole value of this piece is that one API write triggers the trusted chain.

### Q2 (the double-fire guard) — Is re-adding an existing `closed` tag a no-op?
Our outbox can retry a write after a lost ack. If re-adding a tag that's already present **re-fires** the "Contact Tag" workflow, a retry sends a **second welcome SMS / creates a second sub-account contact**. GHL's "Contact Tag added" trigger normally fires only on the absent→present transition (re-adding an existing tag does nothing), which would make the write naturally idempotent — but this must be verified, because our whole retry-safety story rests on it. (Even if it *does* re-fire, our planned **read-before-write** guard — GET the contact's tags, skip the add if `closed` is present — covers it. Confirming Q2 just tells us whether that guard is essential or belt-and-suspenders.)

### Q3 (scope) — Is `closed` the only tag we must write?
Confirm the exact tag string (`closed`? `Closed`? case-sensitive?) and whether the downstream chain keys on `closed` alone, or also needs `onboarded` / `onboarded2` / `paidclient` written by the app. The design's allowlist starts at just `closed`; confirm that's sufficient for the first-payment step.

### Q4 (only if the answer to "do we add an inbound GHL webhook?" is yes) — the loop-safety question
If, and only if, this spike decides the app **should** start receiving inbound GHL webhooks (e.g. to mirror tag/stage changes made by humans in GHL back into SQL), then the echo-loop risk becomes real and the `GhlWriteMarker` design applies. In that case, answer: **does GHL's outbound webhook payload carry any app-settable correlation field** (so we can recognize our own write), or only the contact + tag? If only contact + tag, the marker must dedupe on `(contactId, tag)` within a bounded time window. **Default assumption: we do NOT add an inbound GHL webhook for this wedge, so Q4 is out of scope.**

---

## Exactly what to check in GHL

**A. Confirm the `closed` → chain trigger (Q1, Q3)**
1. In the Detail Launch location, open the workflow that fires on the `closed` tag (from the inventory: the "Tag Closed → …" / onboarding-automation workflow, and the sub-account/welcome-SMS chain it kicks off).
2. Open its trigger. Record: trigger type (expect "Contact Tag"), the **exact tag string** it matches, and any additional filters.
3. Note every downstream action it fires (sub-account contact create, welcome SMS, `onboarded2`, etc.) so we know what one `closed` write sets in motion.

**B. Test an API tag add (Q1, Q2)** — use a throwaway/test contact, not a real client.
1. Get a test contact's id in the Detail Launch location.
2. `POST https://services.leadconnectorhq.com/contacts/{contactId}/tags` with body `{ "tags": ["closed"] }`, using the location's API token (the same token the app's `Ghl:Locations:0:Token` uses) and the API version header the app pins.
3. Confirm: the tag appears on the contact **and** the downstream workflow fired (welcome SMS queued / sub-account contact created).
4. **Re-run the exact same POST** (tag already present). Confirm the workflow did **not** fire a second time. Record the result — this is the Q2 answer.
5. Clean up the test contact.

**C. Decide the inbound-webhook question (Q4 gate)**
1. Confirm (as the code shows) the app has no GHL inbound webhook today.
2. Decide: does this wedge need one? For the `closed` write path, **no** — the app initiates the write and already knows it happened. Only revisit if there's a separate need to mirror human-made GHL changes into SQL.

---

## Decision matrix — findings → what we build

| Finding | What it means | Build |
|---|---|---|
| Q1: API tag add fires the chain ✅ | The automation works as intended | Add `GhlGateway.AddContactTagAsync` + a `WriteGhlTag` outbox action; enqueue it in the first-payment promotion. |
| Q2: re-add is a no-op ✅ | Write is naturally idempotent | Read-before-write becomes belt-and-suspenders (keep it anyway — cheap). |
| Q2: re-add **re-fires** ❌ | Retry could double-send | Read-before-write is **mandatory**: GET tags, skip the add if `closed` present. (Already planned — Eng-Review Hardening #2.) |
| Q1: API tag add does **not** fire the chain ❌ | Can't rely on the tag alone | Fall back to `TriggerWorkflowAsync` (already in the gateway) to invoke the onboarding workflow directly. |
| Q4: not adding an inbound GHL webhook (default) | No echo loop exists | **Drop `GhlWriteMarker` entirely** — it solves a problem this architecture doesn't have. |
| Q4: adding an inbound GHL webhook (only if decided) | Echo loop becomes real | Build `GhlWriteMarker` with the match key the payload supports; add a GHL webhook endpoint + signature verify. |

---

## What this unblocks

With Q1–Q3 answered, the final piece of B is small and spike-clean:
1. `GhlGateway.AddContactTagAsync(contactId, tag)` (POST `/contacts/{id}/tags`) + hardcoded tag allowlist (just `closed`).
2. A `WriteGhlTag` outbox action, enqueued inside the first-payment promotion (`StripeWebhookIngestor.PromoteConversionAsync`), with read-before-write idempotency.
3. Chain-observability follow-up (outside-voice #5): after the write, verify the sub-account contact appeared, so the "SQL/Stripe/GHL stay consistent" success criterion is checkable rather than asserted.

No `GhlWriteMarker`, no inbound GHL webhook, unless Q4 is explicitly answered "yes."

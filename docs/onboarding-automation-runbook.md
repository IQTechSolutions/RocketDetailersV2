# Onboarding automation — how it works now (runbook)

**As of 2026-07-26.** Covers the Convert→Bill→Close wedge: what the app automates, what a human still does, and exactly when each step happens.

**Scope:** this automates the **back half** of the funnel — trial close → billing → onboarding kickoff. The front funnel (ad → RocketDetailer.com form → `/book-now` appointment → SMS confirmations) is unchanged and still runs entirely in GoHighLevel's existing workflows. Nothing in this document touches those.

**Big caveat up front:** only **own-account** clients are automated (flat service-fee subscription). **Master-account clients are NOT automated** — the app will refuse to draft their billing and tell you so. See [Not automated](#what-is-still-manual).

---

## 1. The flow at a glance

```
        HUMAN                                APP                                 EXTERNAL
  ─────────────────────────────────────────────────────────────────────────────────────────
  Sales call: vet fit,
  set up FB page + ad
  account (screen-share)
          │
          ▼
  Click "Convert…"          ──▶  ConvertIntent created (Drafted)
  on the client page             AccountType written to Client
  (own vs master, package)       Draft computed (no Stripe call)
          │                                │
          │                                ▼
          │                      "Pending conversion" panel:
          │                      ✅ ready  or  ⚠ blockers
          ▼
  Review the draft.
  Click "Approve & bill"    ──▶  Create Stripe customer (if none)  ──▶  Stripe
                                 Write IdentityLinks
                                 Create subscription
                                   · Idempotency-Key: convert-{id}
                                   · metadata.convert_intent_id
                                 State → AwaitingPayment
                                            │
                                            │        client pays first invoice
                                            ▼                    │
                              invoice.paid webhook  ◀────────────┘   Stripe
                                 · correlate by subscription id
                                 · State → Paid
                                 · TrialPeriod → Promoted
                                 · record GHL contact as close-target
                                            │
                                            ▼
                              close-write job (every 5 min)
                                 · GET contact tags
                                 · skip if `close` present
                                 · POST {"tags":["close"]}      ──▶  GHL
                                 · State → Closed                      │
                                                                       ▼
                                                          "Tag Closed → Zapier"
                                                                       │
                                                                       ▼
                                                          Zapier: sub-account +
                                                          welcome SMS  (outside our app)
          │
          ▼
  Onboarding call, intake
  form, human-hook video,
  ClickUp form  ── all still manual
```

**Two human decisions gate all money:** clicking **Convert** (intent) and clicking **Approve & bill** (the charge). Everything between and after is automatic. The app never decides to bill anyone.

---

## 2. Step by step — who, what, when, where

### Step 0 — One-time setup (before the first conversion)

| # | Action | Where | Why |
|---|---|---|---|
| 0.1 | Create packages and **set a Stripe Price** on each | **Administration → Packages** (`/admin/packages`) | Without a Stripe Price the draft is blocked. This is the price the own-account service fee bills against. |
| 0.2 | Confirm the client has a **linked GHL contact** | Mapping / Reconciliation | Without it the `close` tag can't be written; the job now raises an investigation and flags it in the Conversions queue rather than failing silently. |
| 0.3 | Confirm config flags | app config | See [Configuration](#6-configuration-reference). |

Setting a price **adds a new effective-dated `PackageVersion`** — it never rewrites history. Old conversions keep the price they were billed at.

### Step 1 — Sales call (human, unchanged)

Vet the client, set up their Facebook page and ad account over screen-share, agree the package. **Nothing to do in the app yet.**

### Step 2 — Record the conversion (human, in the app)

**When:** the moment the client agrees to subscribe.
**Where:** the client's page (`/clients/{id}`) → **Convert…** button (operator/admin only; shows only for `ContractType = Trial` clients).

In the dialog:
- **Account type** — Own (flat service fee) or Master (covers ad spend). Choosing Master will record the intent but **warns that master billing isn't automated**.
- **Package** — defaults to the client's package if set.

**What the app does immediately:**
- Creates a `ConvertIntent` in state **`Drafted`**.
- Writes the chosen **`AccountType` through to the Client record** (never left to the enum default).
- Computes the draft — the exact Stripe action it *would* take — **without calling Stripe**.

**Guards that will refuse the click:**
- client not found;
- client is a merged/retired duplicate → convert the survivor;
- client is not a `Trial` → *"already a subscriber"*;
- a conversion is already in flight (one active intent per client);
- the client **already has a completed (`Paid`/`Closed`) conversion** → *"already been converted and billed"*. This is the double-billing guard and it works even if `ContractType` is stale.

### Step 3 — Review the draft (human, in the app)

**When:** immediately after Step 2, or any time before billing.
**Where:** the **"Pending conversion"** panel on the client page.

It shows one of two things:

- ✅ **Green** — *"Would create a subscription on `price_X` for customer `cus_Y` (or a new Stripe customer) (USD)."* Ready to bill.
- ⚠️ **Amber with a blocker list** — one or more of:
  - *Master-account billing … isn't automated yet* → handle that client manually.
  - *Client bills in {CUR}; automated conversion is USD-only.*
  - *No package selected* → pick one.
  - *The selected package has no Stripe price set* → fix in **Administration → Packages**.

The draft **recomputes live** — set the price in Packages and the panel goes green on reload; no need to redo the Convert.

### Step 4 — Bill it (human, in the app — this is the money moment)

**When:** once the draft is green and you're satisfied.
**Where:** the Pending conversion panel → **Approve & bill** (operator/admin only). A confirmation dialog states plainly that this creates a **real** Stripe subscription.

**What the app does, in order:**
1. Re-checks the kill switch, that the intent is still `Drafted`, and that the draft is still ready.
2. **Creates the Stripe customer** if the client has none — and writes the `Customer` IdentityLink *immediately*, so a webhook arriving seconds later can still resolve the client.
3. **Creates the subscription** with:
   - `Idempotency-Key: convert-{intentId}` — a double-click or retry resolves to **one** subscription at Stripe, never two charges.
   - `metadata.convert_intent_id` — how the first payment is later correlated back to this conversion.
4. Writes the `Subscription` IdentityLink; sets `StripeSubscriptionId`.
5. Moves the intent to **`AwaitingPayment`** and sets `ExpiresAt` = **now + 7 days**.

If two people click at once, the loser gets *"just billed by another action — no double charge (idempotent)"*.

### Step 5 — Client pays (automatic)

**When:** whenever the client pays the first invoice — minutes or days later.
**Trigger:** the `invoice.paid` Stripe webhook (`POST /webhooks/stripe`).

In **one transaction**, the app:
- Correlates the invoice to the conversion via the **subscription id**.
- Moves the intent **`AwaitingPayment` → `Paid`** (also recovers **`Expired` → `Paid`**, see Step 6).
- Moves the client's active `TrialPeriod` to **`Promoted`** — so `EligibilityPolicy` stops suppressing enforcement and billing enforcement takes over with no gap.
- Flips the **Client `Trial` → `Paid`** — they're a subscriber now. This also hides the Convert button and arms the guard that stops a second conversion (and a second charge). `ContractType` is display + audit-snapshot only, so no enforcement decision changes.
- Records the client's **GHL contact** as the `close`-write target.
- Writes the money-in ledger entry (as before).

Idempotent: redeliveries, poison-replays, and later **renewal** invoices never re-promote (the state has moved on).

### Step 6 — Unpaid conversions get reaped (automatic)

**When:** hourly (`convert-expiry-sweep`).
Conversions sitting in `AwaitingPayment` past their `ExpiresAt` (7 days) move to **`Expired`**.

**A late payment still works:** if the client pays *after* expiry, the webhook recovers the intent **`Expired` → `Paid`** and onboarding proceeds. Expiring is not a dead end.

> ⚠️ There is currently **no screen listing expired/unpaid conversions** — see [Gap 3](#gap-3-no-list-view-of-conversions).

### Step 7 — Onboarding fires (automatic)

**When:** within 5 minutes of the payment (`convert-close-write` job).
**Where:** GHL.

For each `Paid` conversion still awaiting its tag:
1. **Resolve the GHL contact** — using the one recorded at payment, or re-resolving now (so a contact linked *after* the payment is picked up automatically).
   - **No contact at all?** Raise a deduped investigation (*"paid but no linked GHL contact"*) and leave it `Paid`. It shows in the Conversions queue and Operations rather than disappearing.
2. **GET** the contact's tags.
3. **Skip the write if `close` is already present** (mandatory — GHL does not guarantee re-adding a tag is a no-op, and a re-add would double-fire onboarding).
4. Otherwise **POST** `{"tags":["close"]}`.
5. Move the intent to **`Closed`**.

A GHL error leaves the conversion `Paid` and the next pass retries; one bad contact never stalls the batch.

Adding `close` fires the published GHL workflow **"Tag Closed → Send Webhook to Zapier"**, and Zapier does the actual onboarding (sub-account creation + welcome SMS).

**Important:** the app can prove it wrote `close` and that GHL fired the Zapier hook. It **cannot see what Zapier did**. Confirm onboarding by observing the client (SMS received, sub-account exists), not by trusting the app's `Closed` state alone.

### Step 8 — Everything after onboarding kickoff (human, unchanged)

Onboarding call, granting sub-account access, the intake form, the human-hook video, the business-info doc, the ClickUp form, and ad creation are **all still manual**.

### Reversing a conversion (human, any time after billing)

**Where:** Pending conversion panel → **Cancel subscription** (shows for `AwaitingPayment` / `Paid`).
Cancels the Stripe subscription and moves the intent to **`Reversed`**. Recurring billing stops immediately. Idempotent (an already-gone subscription counts as done). Not kill-switch gated — stopping a charge only ever reduces exposure.

> Refunds/chargebacks are **not** auto-detected. If one arrives, use **Cancel subscription** to stop future billing. A `close` tag already written is deliberately **not** un-written (that would fire destructive downstream automations) — reverse onboarding by hand in GHL if needed.

---

## 3. State reference

```
  Drafted ──▶ AwaitingPayment ──▶ Paid ──▶ Closed
     │              │                         │
     │              ├──▶ Expired ──▶ Paid     └──▶ Reversed
     │              │    (late payment recovers)
     │              └──▶ Failed
     └── never billed without a human clicking Approve & bill
```

| State | Meaning | What moves it on |
|---|---|---|
| `Drafted` | Human recorded intent; nothing billed | **Approve & bill** (human) |
| `AwaitingPayment` | Live Stripe subscription; waiting on first payment | `invoice.paid` webhook, or expiry sweep |
| `Paid` | First payment received; trial promoted | close-write job |
| `Closed` | `close` tag written; onboarding kicked off | terminal (or Cancel → `Reversed`) |
| `Expired` | Billed but unpaid past the window | a late payment (→ `Paid`) |
| `Failed` | Reserved for payment failure | — |
| `Reversed` | Subscription canceled by a human | terminal |

Only **one active** (non-terminal) intent per client — enforced by a filtered unique index.

---

## 4. Where operators watch it

- **Conversions** (`/conversions`) — every conversion and what needs attention. The daily driver.
- The client page's **Pending conversion** panel — the single client's draft + actions.
- **Operations** — investigations raised by the close-write job.

## 5. Background jobs

| Job | Schedule | What it does |
|---|---|---|
| `convert-expiry-sweep` | hourly (`0 * * * *`) | `AwaitingPayment` past `ExpiresAt` → `Expired` |
| `convert-close-write` | every 5 min (`*/5 * * * *`) | `Paid` → write `close` tag → `Closed` |
| `stripe-sync` / webhooks | 15 min / realtime | payments, invoices, subscriptions |
| `policy-evaluation` | every 5 min | enforcement heartbeat (unchanged) |

---

## 6. Configuration reference

| Key | Current | Effect |
|---|---|---|
| `Convert:CloseTagWriteEnabled` | **`true`** (set in `appsettings.json`) | `false` = the close-write job no-ops entirely (no GHL writes). |
| `Safety:GhlTestMode` | **`true`** (pinned in `appsettings.json`) | `true` = **every** GHL write redirects to the configured test contact. Real clients are never touched. |
| `Safety:TestContactId` / `Safety:TestContactLocationId` | must be set | Required when TestMode is on, or the write **throws** rather than risk a real client. |
| `Enforcement:DefaultDunningLocationId` | check | The GHL location used for the close write (shared with dunning). Moot under TestMode. |

> Runtime **user-secrets / environment variables override `appsettings.json`.** Verify with
> `dotnet user-secrets list --project src/RD.Web` before trusting the table above.

### Go-live sequence

1. **Now (test):** `CloseTagWriteEnabled = true`, `GhlTestMode = true` → close writes land on the **test contact**. Run one real conversion end to end and confirm the test contact receives the welcome SMS / sub-account.
2. **Then (live):** set `GhlTestMode = false` and restart → writes hit real clients. Watch the first real conversion closely.

---

## 7. What is still manual

- **Master-account (Meta-flagged) clients' billing** — their subscription covers *variable* ad spend, which a fixed Stripe Price can't express. Deferred to its own wedge (recorded in `TODOS.md`). The draft will tell you to handle it manually.
- Sales call, vetting, FB page + ad account setup.
- The **22-field ClickUp onboarding form**.
- Onboarding call, sub-account access grant, intake form, human-hook video, business-info doc.
- **Ad creation** (virtual assistant).
- Refund/chargeback detection (use Cancel subscription).
- Auto-promotion to unattended billing ("rung C") — deliberately not built.

---

## 8. Known gaps and operational warnings (all three now fixed)

### Gap 1 — Double-billing a converted client — ✅ **FIXED** (`b6fc982`)

**Was:** nothing set `Client.ContractType = Paid` after a conversion, so the Convert button stayed visible, the `ContractType != Trial` guard never fired, and once the first intent went terminal a second Convert could create a **second live Stripe subscription** (different idempotency key, so Stripe won't collapse it) — billing the client twice.

**Now, two layers:**
1. The first-payment promotion flips the client **Trial → Paid** in the same transaction as the intent/trial promotion. The Convert button hides itself and the `ContractType` guard bites. (`ContractType` is display + audit-snapshot only — `EligibilityPolicy` never branches on it — so this changed no enforcement behavior.)
2. A `ContractType`-independent guard in `CreateIntentAsync`: **refuse when the client already has a `Paid`/`Closed` intent.** This catches any client converted *before* the fix (who still reads `Trial`) and any later data drift.

**Side effects worth knowing:**
- A client whose conversion was **`Reversed`** (billed, then canceled) now reads `Paid` and is **not** re-convertible. A deliberate re-subscribe needs an admin to reset `ContractType` — blocking is the safe default.
- **`Expired`/`Failed`** conversions never paid, so those clients stay `Trial` and **can** be converted again. Correct: an unpaid conversion should be retryable.

### Gap 2 — Paid conversion with no GHL contact — ✅ **FIXED**

**Was:** the close-write job only picked up conversions that already had a GHL contact resolved, so a client with no linked contact sat in `Paid` **forever** — no tag, no onboarding, no alert.

**Now** the job looks at *every* `Paid` conversion awaiting its tag and:
1. **Self-heals** — re-resolves the GHL contact, so a contact linked *after* the payment landed is picked up on the next pass and the conversion completes normally.
2. If there's still no contact, raises a **deduped investigation** (one open item per client, `UnmappedIdentity`, system GHL) reading *"Conversion is paid but the client has no linked GHL contact…"*, so it surfaces in the work queue instead of vanishing.

The conversion stays `Paid` (correct — onboarding genuinely hasn't fired) and is flagged in the [Conversions queue](#conversions-queue).

### Gap 3 — No list view of conversions — ✅ **FIXED**

<a id="conversions-queue"></a>
**Now:** a **Conversions** page (`/conversions`, in the main nav) lists every conversion newest-first — client, state, account type, start time, Stripe subscription — with a **"Needs attention"** column and a count in the header. It flags only what a human must act on:

| Flag | Meaning |
|---|---|
| *Billed but never paid — expired.* | `Expired` — chase or re-convert |
| *Paid but no GHL contact linked…* | onboarding can't fire — link the contact ([Gap 2](#gap-2--paid-conversion-with-no-ghl-contact--fixed)) |
| *Paid — waiting on the `close` tag write…* | normal for <5 min; persistent means the job is off/blocked |
| *Payment window has lapsed…* | the next hourly sweep will expire it |

Healthy and completed conversions show no flag, so the queue stays signal, not noise. **Check this page daily** — it is where Gaps 1–2 would surface.

### Other notes

- **Zapier is a black box to this app** (Step 7). `Closed` means "we wrote the tag", not "the client was onboarded".
- Adding `close` also fires a second workflow, **"Closed Deal Webhook Trigger"** — historically pointed at a dead URL (`/webhook-trigger/undefined`); reported fixed 2026-07-26.
- GHL has **"Allow re-entry"** enabled on the close workflow: deliberately removing and re-adding `close` **will** fire onboarding again. Useful for a genuine re-onboard; dangerous by accident.
- **USD only** — non-USD clients are refused at draft time (matching the enforcement policy's currency guard).

---

## 9. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Convert… button missing | Client isn't `Trial`, is a merged duplicate, or you lack the operator role | Check the contract chip / role |
| Draft shows *"no Stripe price"* | Package has no `StripePriceId` | **Administration → Packages** → Set price |
| Draft shows *"Master-account … isn't automated"* | Master-account client | Handle billing manually |
| Draft shows *"USD-only"* | Client currency isn't USD | Out of scope for v1 |
| Approve & bill says *"kill switch engaged"* | Global stop is on | Release it in Operations |
| Stuck in `AwaitingPayment` | Client hasn't paid | Wait, or chase (dunning); it expires after 7 days |
| Stuck in `Paid`, never `Closed` | Close-write disabled, kill switch on, no GHL contact, or GHL errors | Check the **Conversions** page for the reason, then `Convert:CloseTagWriteEnabled`, the kill switch, the contact link, and the job logs |
| `Closed` but client reports no SMS | Zapier side | Check the Zap — the app's job ends at the tag write |
| Client billed twice | Should now be prevented ([Gap 1](#gap-1--double-billing-a-converted-client--fixed-b6fc982)). If it still happens, cancel the duplicate subscription in Stripe and report it. |

---

## 10. Where the code lives

| Concern | File |
|---|---|
| Intent record + states | `src/RD.Domain/Entities/ConvertIntent.cs`, `Enums.cs` |
| Draft logic (pure, unit-tested) | `src/RD.Domain/ConvertDrafter.cs` |
| Convert + draft read | `src/RD.Web/Services/ConvertService.cs` |
| Bill + cancel | `src/RD.Web/Services/ConvertBillingService.cs` |
| Price book | `src/RD.Web/Services/PackageAdminService.cs`, `Components/Pages/PackagesAdmin.razor` |
| Stripe writes | `src/RD.Infrastructure/Gateways/StripeGateway.cs` |
| GHL tag read/write | `src/RD.Infrastructure/Gateways/GhlGateway.cs` |
| First-payment promotion | `src/RD.Infrastructure/Webhooks/StripeWebhookIngestor.cs` |
| Expiry sweep / close write | `src/RD.Infrastructure/Sync/ConvertExpirySweepJob.cs`, `ConvertCloseWriteJob.cs` |
| UI | `src/RD.Web/Components/Pages/ClientDetailPage.razor`, `Components/Dialogs/ConvertToSubscriberDialog.razor` |
| Design + spike | `docs/spikes/ghl-closed-tag-write-spike.md`, the approved office-hours design doc |

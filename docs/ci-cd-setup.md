# CI / live deploy — setup

The pipeline lives in `.github/workflows/ci-cd.yml`. It does **not** work until the three things below exist. Nothing here touches production until you complete step 2 **and** approve a run.

**Shape:** every push and PR builds and runs both test suites. A merge to `main` also publishes a versioned artifact, then **waits for a human approval** before deploying that exact artifact to IIS through a self-hosted runner on the live box.

> ⚠️ **A live deploy migrates the production database.** The app calls `db.Database.MigrateAsync()` on startup, so any pending EF migration is applied the moment the app pool restarts. The approval gate exists precisely because of this. Rollback restores *files*, never the database.

---

## 1. Install the self-hosted runner on the live box

Chosen because the live server sits on a private/Tailscale address that GitHub's cloud runners can't reach. The deploy runs locally, so no inbound access and no VPN credentials in CI.

1. GitHub → **Settings → Actions → Runners → New self-hosted runner → Windows**.
2. Follow the given commands on the live box.
3. When asked for labels, add **`windows`** (the workflow targets `[self-hosted, windows]`).
4. Install it **as a service** so deploys work when nobody is logged in.
5. The service account needs to:
   - write to the IIS physical path;
   - stop/start the app pool — i.e. be a local Administrator, or hold explicit IIS permissions. Without this the *Recycle the app pool* step fails.

Verify: the runner shows **Idle** in GitHub.

## 2. Create the `live` environment with an approval gate

This is the safety gate. **Do this before the first merge to `main`,** or a merge will deploy unreviewed.

1. GitHub → **Settings → Environments → New environment** → name it exactly **`live`**.
2. Enable **Required reviewers** and add yourself (and anyone else who should be able to release).
3. Optionally restrict deployments to the `main` branch.

Result: after CI passes on `main`, the deploy job sits in *Waiting* until a reviewer approves.

## 3. Set the repository variables

**Settings → Secrets and variables → Actions → Variables** (these are *variables*, not secrets — none is sensitive):

| Variable | Example | What it's for |
|---|---|---|
| `IIS_APP_POOL` | `RocketDetailersV2` | App pool to stop/start |
| `IIS_PHYSICAL_PATH` | `C:\inetpub\wwwroot\rocketdetailers` | Where the site's files live |
| `LIVE_URL` | `https://control.example.com` | Post-deploy health check + the environment link. Omit to skip the check. |

The deploy fails fast with a clear message if the first two are missing.

---

## Secrets: deliberately not in the pipeline

The live box owns its own configuration (environment variables / user-secrets). The pipeline ships **code only** — no production credentials ever enter GitHub.

Consequences to respect:

- `appsettings.Production.json` on the box is **never overwritten** (excluded from the file copy), so live config survives deploys.
- `appsettings.json` **is** shipped from git. Anything the box needs to override must live in `appsettings.Production.json` or an environment variable, both of which win.
- The `Convert:CloseTagWriteEnabled` and `Safety:GhlTestMode` flags currently sit in the repo's `appsettings.json`. **Deploying will ship those values.** If live should differ, set them on the box before the first deploy — see the onboarding runbook.
- The repo's `appsettings.json` still contains a plaintext SQL connection string. That is tracked separately as a rotation task and should be fixed regardless of CI.

## What a deploy actually does

1. Verifies the required variables are set.
2. Downloads the artifact built and tested on this commit (not a rebuild).
3. Backs up the current deployment to the runner's temp directory.
4. Writes `app_offline.htm` so IIS drains and releases file locks.
5. Mirrors the publish output with `robocopy /MIR`, excluding `appsettings.Production.json`, `logs`, `App_Data`.
6. Stops and starts the app pool.
7. Removes `app_offline.htm`.
8. Polls `LIVE_URL` up to 10 times; the first request triggers startup **and the migrations**.
9. On failure, restores the file backup and recycles the pool. **Database migrations are not reverted.**

## Testing it safely

1. Push a branch and open a PR — confirm build + both test suites pass. No deploy happens on PRs.
2. Merge to `main` — CI runs, then the deploy job enters *Waiting*.
3. **Before approving**, confirm the pending migrations are ones you want applied to live.
4. Approve, then watch the health check and the run summary.

## Known limitations

- **Brief downtime** during the copy and app pool recycle (seconds). No blue/green or slot swap.
- **No database rollback.** A bad migration needs a forward fix or a restore from backup.
- **Single runner** = deploys are serialised (a `concurrency` group also enforces this).
- **Integration tests need a SQL instance.** CI starts LocalDB on the hosted Windows runner; `RD_TEST_SQL` overrides the server if you ever need to point them elsewhere.

# CI and live deployment

The workflow in `.github/workflows/ci-cd.yml` builds and tests every pull
request. When a pull request is merged into `main`, the resulting `push` event
publishes a self-contained Windows artifact and deploys that exact artifact to
the live server.

The live topology is:

1. IIS ARR owns the public bindings for `rocketdetailer-ai.com`.
2. ARR proxies to `http://127.0.0.1:3000`.
3. The Windows service `RocketDetailerPaymentAutomation` runs the shared
   `Start-RocketDetailer.ps1` launcher.
4. The launcher starts whichever release the
   `C:\inetpub\services\rocket-detailer\current` junction selects.

The pipeline does not replace the IIS proxy folder or recycle its app pool.

## GitHub configuration

Create a GitHub environment named `live`, then define:

| Kind | Name | Live value |
| --- | --- | --- |
| Repository variable | `APP_ROOT` | `C:\inetpub\services\rocket-detailer` |
| Repository variable | `WINDOWS_SERVICE` | `RocketDetailerPaymentAutomation` |
| Repository variable | `LIVE_URL` | `https://rocketdetailer-ai.com/health` |
| `live` environment secret | `LIVE_CONNECTION_STRING` | Production SQL Server connection string |
| `live` environment secret | `LIVE_APPLICATION_ENVIRONMENT_JSON` | Flattened .NET credential settings for Stripe, Meta, three HighLevel locations, and Slack |

The connection string and provider bundle are validated, combined, and written
atomically on the server to
`C:\ProgramData\RocketDetailer\secrets\dotnet.application.environment.json`.
Neither is included in the artifact or repository. The provider bundle uses
.NET environment-variable names (double underscores), for example:

```json
{
  "Stripe__ApiKey": "rk_live_replace",
  "Meta__AccessToken": "replace",
  "Meta__AdAccountId": "act_1234567890",
  "Meta__BaseUrl": "https://graph.facebook.com/v25.0",
  "Ghl__Locations__0__LocationId": "replace",
  "Ghl__Locations__0__Token": "pit-replace",
  "Ghl__Locations__1__LocationId": "replace",
  "Ghl__Locations__1__Token": "pit-replace",
  "Ghl__Locations__2__LocationId": "replace",
  "Ghl__Locations__2__Token": "pit-replace",
  "Slack__IncomingWebhookUrl": "https://hooks.slack.com/services/replace/replace/replace"
}
```

Optional `Ghl__Locations__0..2__Name` values may be included. Stripe webhook
and Slack signing secrets are also accepted when those inbound integrations
are configured. Existing optional signing secrets and server-local operational
settings are preserved when omitted; include a signing secret in the bundle
when rotating it. Never put the bundle in a tracked file or command history.
Stale Stripe or HighLevel base-URL overrides are replaced during rotation with
the applications' checked-in official vendor endpoints, including inherited
machine-level overrides when the launcher imports the generated file.

`Stripe__ApiKey` enables outbound Stripe API calls, but inbound
`/webhooks/stripe` events also require `Stripe__Webhook__SigningSecret`.
Likewise, `Slack__IncomingWebhookUrl` enables outbound notifications, while
Approve/Dismiss callbacks require `Slack__SigningSecret` and a server-local
`Slack__UserMap__N__{SlackUserId,Email}` mapping. The deployment warns when a
signing secret is absent; it preserves existing values but cannot derive them
from an API key or incoming-webhook URL.

## Self-hosted runner

Install one Windows x64 GitHub Actions runner on the live server:

1. Open repository **Settings > Actions > Runners > New self-hosted runner**.
2. Follow GitHub's Windows commands from a dedicated directory such as
   `C:\actions-runner\RocketDetailersV2`.
3. Configure the runner with the additional label `live`.
4. Install it as a Windows service and use an account that can write under
   `APP_ROOT` and stop/start `RocketDetailerPaymentAutomation`.
5. Confirm the runner shows **Idle** with labels `self-hosted`, `Windows`,
   `X64`, and `live`.

Before each configuration transaction, the workflow creates or hardens
`C:\ProgramData\RocketDetailer\secrets`. It grants full control only to
`SYSTEM`, local administrators, and the dedicated runner account, plus read
access to the resolved Rocket Detailer service identity. It rejects broad
`Everyone`, authenticated-user, built-in user, or guest access on the
directory, live file, temporary file, and rollback backup. Atomic rotations
preserve the live configuration file's Windows ACL.

The deploy job deliberately targets `[self-hosted, windows, live]`, preventing
an unrelated self-hosted runner from receiving production work.

## Deployment and rollback

For each successful merge:

1. GitHub-hosted Windows CI restores, builds, runs unit tests, and runs
   integration tests against LocalDB.
2. CI publishes a self-contained `win-x64` release.
3. The live runner stages it under `APP_ROOT\releases\<sha>-<attempt>`.
4. The service is stopped and `current` is atomically switched to the new
   release with a directory junction.
5. The service starts on loopback port 3000.
6. CI checks both the loopback and public `/health` endpoints.
7. If either check fails, CI restores both `current` and the previous
   application-environment file before restarting the previous release.

The previous application-environment file remains beside the live file with a
`.previous` suffix until both health checks pass. A leftover backup blocks the
next deployment rather than overwriting recovery evidence; inspect and recover
that transaction manually before retrying.

Before changing `current`, the runner also writes a non-secret release journal
to `APP_ROOT\shared\service\deploy-transaction.json`. Rollback validates the
previous release before removing the failed link, and the journal survives a
runner crash or hard timeout so the next deployment fails closed instead of
discarding recovery context. Failure and cancellation trigger rollback; the
health step has its own eight-minute ceiling to leave cleanup headroom.

The shared launcher retains a Node fallback so the first .NET deployment can
still roll back to the pre-.NET production release.

## Database warning

`Program.cs` applies pending EF Core migrations during startup. A file rollback
cannot undo a database migration. Migrations therefore need to remain
backward-compatible with the immediately previous release, and a production
database backup should exist before merging migration-bearing pull requests.

## Production credential rotation

Rotate provider credentials by replacing the complete
`LIVE_APPLICATION_ENVIRONMENT_JSON` value in the `live` GitHub environment,
then deploy the verified commit. The deployment contract rejects an incomplete
bundle before the service is stopped, while preserving server-local settings
that are not part of credential rotation. Managed Stripe API, Meta, GHL
locations, and Slack webhook keys are replaced as a set, so stale extra GHL
locations do not survive the rotation. Validate new credentials with
read-only provider calls before installing the bundle; Slack incoming webhooks
can only be shape-checked without posting a message.

A SQL connection string was previously committed in `appsettings.json`. The
tracked file now contains an empty value, but Git history still contains the old
credential. Rotate that SQL login, update `LIVE_CONNECTION_STRING`, and revoke
the old password.

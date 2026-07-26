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

The connection string is written on the server to
`C:\ProgramData\RocketDetailer\secrets\dotnet.application.environment.json`.
It is not included in the artifact or repository.

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
7. If either check fails, CI switches `current` back and restarts the previous
   release.

The shared launcher retains a Node fallback so the first .NET deployment can
still roll back to the pre-.NET production release.

## Database warning

`Program.cs` applies pending EF Core migrations during startup. A file rollback
cannot undo a database migration. Migrations therefore need to remain
backward-compatible with the immediately previous release, and a production
database backup should exist before merging migration-bearing pull requests.

## Production credential rotation

A SQL connection string was previously committed in `appsettings.json`. The
tracked file now contains an empty value, but Git history still contains the old
credential. Rotate that SQL login, update `LIVE_CONNECTION_STRING`, and revoke
the old password.

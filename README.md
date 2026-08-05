# RocketDetailersV2

A financial control plane for a detailing-niche ad agency, built over GoHighLevel, Stripe, Meta Ads, and ClickUp. It is not a CRM replacement: SQL Server is the source of truth for money and enforcement state, and billing→ads enforcement ships shadow-first with a per-client Shadow → Assist → Auto ladder.

**Stack:** Blazor Server · EF Core · SQL Server · Hangfire. Single-tenant, self-hosted.

## Solution layout

Solution file: `RocketDetailers.slnx`

| Project | Purpose |
| --- | --- |
| `src/RD.Domain` | Domain entities, enums, and enforcement policy (no infrastructure dependencies) |
| `src/RD.Infrastructure` | EF Core persistence and migrations, provider gateways, sync, webhooks, enforcement |
| `src/RD.Web` | Blazor Server app — cockpit UI, webhook endpoints, background jobs |
| `src/RD.Tools.Import` | CLI for imports and sync runs |
| `tests/RD.Tests` | Unit tests |
| `tests/RD.Tests.Integration` | Integration tests |

## Build and test

Requires the .NET SDK and, for running the app, a SQL Server instance.

```bash
dotnet build RocketDetailers.slnx
```

```bash
dotnet test RocketDetailers.slnx
```

Configuration (connection strings, provider API keys) is supplied via user secrets or environment variables — never committed to the repo.

### Email (password resets)

Self-service password reset needs an SMTP relay. Without one the app runs fine — the forgot-password page tells users to ask an Admin instead of promising mail it can't send.

```bash
dotnet user-secrets set "Email:Host" "smtp.example.com" --project src/RD.Web
```

| Key | Notes |
| --- | --- |
| `Email:Host` | Relay host. Empty ⇒ email disabled |
| `Email:Port` | Default `587` |
| `Email:UseStartTls` | Default `true`; turn off only for a local mail catcher |
| `Email:UserName` / `Email:Password` | Secrets. Empty user ⇒ anonymous relay |
| `Email:FromAddress` / `Email:FromName` | Required once `Host` is set |

Submission is STARTTLS on 587 (implicit TLS on 465 and XOAUTH2 are not supported — swap `SmtpEmailSender` for MailKit if a relay ever requires them). Reset links are credentials and are never written to logs, so point a local catcher such as smtp4dev at `Email:Host` to see them in development.

### Versioning

The repo-root `VERSION` file (`MAJOR.MINOR.PATCH.MICRO`) is the single source of truth for assembly versions. `Directory.Build.props` reads it into every project's `VersionPrefix`, `AssemblyVersion`, and `FileVersion`; `Directory.Build.targets` fails the build if the file is missing or malformed. Bump it via the ship workflow rather than editing project files.

## Documentation

- [Design doc](docs/designs/rocket-detailer-control-plane.md) — vision, scope decisions, and milestone plan for the control plane
- [CHANGELOG.md](CHANGELOG.md) — notable changes per milestone
- [TODOS.md](TODOS.md) — deliberately deferred work, with context
- [CLAUDE.md](CLAUDE.md) — project instructions for AI-assisted development

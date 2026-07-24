# RocketDetailersV2

Automation application for Rocket Detailer — a financial control plane over GoHighLevel, Stripe, Meta Ads, and ClickUp for a detailing-niche ad agency. Blazor Server + EF Core + SQL Server + Hangfire, single-tenant, self-hosted.

Approved design doc: `~/.gstack/projects/IQTechSolutions-RocketDetailersV2/ivanr-main-design-20260724-000239.md` (office-hours, 2026-07-24). Core decisions: control plane not CRM replacement; billing→ads enforcement wedge ships shadow-first with a per-client Shadow→Assist→Auto ladder; SQL is source of truth; secrets never committed.

## Docs index

- `README.md` — overview, solution layout, build/test
- `docs/designs/rocket-detailer-control-plane.md` — in-repo design doc (vision, scope, milestones)
- `CHANGELOG.md` — notable changes per milestone
- `TODOS.md` — deliberately deferred work, with context

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool. When in doubt, invoke the skill.

Key routing rules:
- Product ideas/brainstorming → invoke /office-hours
- Strategy/scope → invoke /plan-ceo-review
- Architecture → invoke /plan-eng-review
- Design system/plan review → invoke /design-consultation or /plan-design-review
- Full review pipeline → invoke /autoplan
- Bugs/errors → invoke /investigate
- QA/testing site behavior → invoke /qa or /qa-only
- Code review/diff check → invoke /review
- Visual polish → invoke /design-review
- Ship/deploy/PR → invoke /ship or /land-and-deploy
- Save progress → invoke /context-save
- Resume context → invoke /context-restore
- Author a backlog-ready spec/issue → invoke /spec

# Changelog

All notable changes to this project are documented in this file.

Format: `## [MAJOR.MINOR.PATCH.MICRO] - YYYY-MM-DD` with Added / Changed / Fixed / Removed sections.

## [0.0.0.1] - 2026-07-24

### Fixed

- Resolved a high-severity security advisory (GHSA-5crp-9r3c-p9vr): the app previously shipped Newtonsoft.Json 11.0.1 pulled in through Hangfire. All projects that consume RD.Infrastructure (RD.Web, RD.Tools.Import) now resolve Newtonsoft.Json 13.0.4 via a single pin there, so builds are clean of the NU1903 audit warning and future consumers of RD.Infrastructure inherit the safe version automatically.

# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [docs/versioning.md](docs/versioning.md).

## [Unreleased]

## [0.1.0] - 2026-11-17

First public release.

### Added

- A NuGet proxy that holds back package versions newer than a cooldown, with waivers per organisation, and tokens for restores.
- SQLite or PostgreSQL storage; expand-only database updates; `packmoat-admin` for setup, migrations and checks.
- Deployment kits: one server (Docker Compose and Caddy), Kubernetes (Helm chart, from a clone) and Azure Container Apps.
- A signed image for linux/amd64 and linux/arm64, with an SBOM per platform and build provenance.
- A specification with shared test cases ([spec/](spec/README.md)).

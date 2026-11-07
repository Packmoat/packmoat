# Packmoat

Packmoat is a package firewall for NuGet. Developers point NuGet at Packmoat instead of at their organisation's Azure Artifacts feed. Packmoat reads that feed on their behalf and hides every package version that has been public for less than a cooldown (14 days by default), so a freshly published malicious version does not reach a build. One server serves many organisations ("tenants"), each with its own feed, settings, tokens and audit trail.

## Quick start

Packmoat runs as one container behind Caddy on a Linux server with Docker, or on Kubernetes with the Helm chart. Follow [docs/self-hosting.md](docs/self-hosting.md) from start to end: it covers DNS, Microsoft Entra ID, settings, adding an organisation, backups and upgrades.

To build and test the code you need the .NET SDK named in [dotnet/global.json](dotnet/global.json):

```
cd dotnet
dotnet test
```

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains how the parts fit together.

## Licence

Packmoat is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first: every commit needs a DCO sign-off. Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).

## Security

Do not report a vulnerability in a public issue. [SECURITY.md](SECURITY.md) says how to report one privately.

## Hosted service

If you would rather not run Packmoat yourself, a hosted service is available at https://packmoat.com.

## A note on references

Numbers like #123 in code comments, tests and docs refer to issues and pull requests in an earlier private tracker, not to this repository's issues or pull requests.

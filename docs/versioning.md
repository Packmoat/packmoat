# Versions and stability

Packmoat uses version numbers of the form MAJOR.MINOR.PATCH. Each release is a git tag `vX.Y.Z` on the main branch, with notes on the [Releases page](https://github.com/Packmoat/packmoat/releases) and in [CHANGELOG.md](../CHANGELOG.md).

## Before 1.0

- **Minor releases (0.1 to 0.2)** may change settings, `packmoat-admin` commands, the interfaces for modules, and the behaviour described in [spec/](../spec/README.md). Every such change is listed in [CHANGELOG.md](../CHANGELOG.md) under **Changed** or **Removed**, with what you must do, and repeated in the release notes under **Upgrade notes**.
- **Patch releases (0.1.0 to 0.1.1)** only fix bugs and security problems. They do not rename or remove a setting or command.
- **Database updates** are always expand-only (see [Upgrades and rollback](operations.md#upgrades-and-rollback)). You may skip patch releases. While copies keep running, upgrade one minor release at a time. Rolling back needs the backup you took before upgrading.

## Images

`ghcr.io/packmoat/packmoat-core` has these tags: `X.Y.Z` (one release; it never moves), `X.Y` (the newest patch of that minor), `latest` (the newest release), `main` (the newest build of the main branch, which is not a release) and `sha-<commit>`. A release tag names the same signed image as the `sha-` tag of its commit, so the `cosign verify` and `gh attestation verify` commands in [Prebuilt image](deploy/single-server.md#prebuilt-image) work unchanged. Run the image by its digest.

## Support

Only the newest release is supported. Fixes land on main first and ship as a patch of the newest minor release. Older minors get no fixes. At 1.0 we will publish what stays compatible.

# Azure Container Apps

**Status:** Supported (pre-production), more than one copy on PostgreSQL (SQLite runs one); tested in CI against PostgreSQL 17. It has not yet been tried on Container Apps with Azure Database for PostgreSQL.

## Why it needs PostgreSQL

By default Packmoat keeps its data in SQLite: one database file, on a local disk that survives restarts, written by exactly one running copy of the server. Container Apps gives a container no such disk: its persistent storage is a network file share (Azure Files), and a SQLite database is not safe on a network file share. So on Container Apps, Packmoat uses a PostgreSQL database instead, with `PACKMOAT_DB_ENGINE=postgres`. [PostgreSQL](../self-hosting.md#postgresql) explains how to set one up, and what the server checks before it starts.

## What it looks like

The same image as everywhere else, with:

- **A PostgreSQL 17 database**, such as Azure Database for PostgreSQL flexible server, with two roles: an owner that runs the migrations, and an app role the server signs in as. Connect directly, not through a pooler in transaction mode (the server's start refuses one). Use `SSL Mode=VerifyFull` with the server's certificate authority given by `Root Certificate`, as [PostgreSQL](../self-hosting.md#postgresql) shows.
- **A migration job.** A Container Apps job, run by hand before each upgrade, that runs `packmoat-admin db migrate --grant-to <app role>` from the new image, signed in as the owner role.
- **Replicas.** More than one replica is supported on PostgreSQL, and so are multiple revision mode and rolling updates: every copy keeps its forms, its limits for signed-in actions, the cookie keys and the job leases in the database, so a request may go to any copy and no sticky sessions are needed. Run the migration job first, then roll out the new revision: see [More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql). Two things to plan for:
  - **Connections.** The database must allow N × `Maximum Pool Size` connections, plus any connections a module holds on its own (the core server holds none; a module that does says so in its docs), where N is the most replicas you will run, counting the old and the new revision during an update. Set `Maximum Pool Size` in the connection string to fit. [Connections and pooling](../self-hosting.md#connections-and-pooling) has the rest.
  - **Limits on traffic nobody has signed in to** (failed sign-ins, token guesses) are counted by each replica on its own, so with N replicas they are N times looser.
- **Container Apps' own HTTPS ingress** in front, so `PORT` and `PACKMOAT_TRUSTED_PROXIES` are set as for [your own proxy](own-proxy.md). Point the ingress's target port at `PORT`.
- **Health probes** on `/healthz` (liveness) and `/readyz` (readiness), with the `Host` header set to `localhost` (see [Health checks](../operations.md#health-checks)).
- **Settings:** `PACKMOAT_DB_ENGINE=postgres`, `PACKMOAT_DB` set to the app role's connection string with `SSL Mode=VerifyFull`, and `PACKMOAT_COOKIE_KEYS=database`, so the keys that protect sign-in cookies live in the database rather than on a disk.
- **Secrets as files:** the master key, the connection string and the other secrets, given through each setting's `_FILE` form.

Never sign the server in as the administrator role that Azure Database for PostgreSQL gives you: it is a member of `azure_pg_admin`, which can do far more than Packmoat needs.

## Backups

`packmoat-admin backup` works on SQLite only. Use the database service's own backups and point-in-time restore, or `pg_dump`, and keep the master key apart from them. A restore needs the same master key.

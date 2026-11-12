# Running Packmoat yourself

This guide has moved. [deploy/README.md](deploy/README.md) lists what every deployment needs and links to a guide for each way to run Packmoat.

The steps for one server with Docker are in [deploy/single-server.md](deploy/single-server.md), and for Kubernetes in [deploy/kubernetes.md](deploy/kubernetes.md). Every setting, backups and upgrades are in [operations.md](operations.md).

One part is new here: [PostgreSQL](#postgresql), below, for running Packmoat on a PostgreSQL database instead of SQLite.

## PostgreSQL

Packmoat keeps its data in SQLite by default: one file on a disk that survives restarts. It can use a PostgreSQL database instead, for a platform that has no such disk, such as Azure Container Apps.

**Status:** Supported (pre-production), with more than one copy of the server. It is tested in CI against PostgreSQL 17. Amazon RDS, Azure Database for PostgreSQL and Google Cloud SQL have not been tried yet.

Set `PACKMOAT_DB_ENGINE=postgres`, and set `PACKMOAT_DB` to a connection string, such as `Host=db.example.internal;Port=5432;Database=packmoat;Username=packmoat_app;SSL Mode=VerifyFull;Root Certificate=/run/secrets/db-ca.crt` with the role's password added. It is `Key=Value` pairs separated by `;`. A `postgresql://user:password@host/database` address, which providers often show, is not accepted: copy its parts into the pairs. Keep it in a secret (`PACKMOAT_DB_FILE`). PostgreSQL also needs `PACKMOAT_COOKIE_KEYS=database` and, as always, `PACKMOAT_MASTER_KEY`. [operations.md](operations.md#postgresql) has the full rules for each setting.

### More than one copy of the server

On PostgreSQL you may run more than one copy of the server against one database; on SQLite exactly one runs (a second is refused). Nothing needs sticky sessions: the one-time values in forms, the cookie keys, the held steps of the Azure setup and the job leases are in the database, and a request can go to any copy.

- Set `PACKMOAT_COOKIE_KEYS=database`, and keep the copies' clocks within a few seconds of the database's (a warning from 2 seconds apart, a refusal from 30).
- Budget the connections: N copies × `Maximum Pool Size`, plus any connections a module holds on its own (the core server holds none; a module that does says so in its docs). See [Connections and pooling](#connections-and-pooling).
- The limits on failed sign-ins and token guesses are counted per copy, so with N copies they are N times looser.
- Upgrade by running `packmoat-admin db migrate` first, then replacing the copies one at a time. Database changes are expand-only, so the previous release keeps serving until the last copy is replaced. [operations.md](operations.md#more-than-one-copy-on-postgresql) has the steps.

### Two roles

Use two PostgreSQL roles (database users):

- **The owner role** owns the database's schema and runs the migrations. It is used only for the database updates, never by the server.
- **The app role** is what the server and the everyday `packmoat-admin` commands sign in as. It may read and write rows, and nothing more.

The reason: an owner can turn off the database rules (triggers) that keep the audit records and the records of deleted organisations. The app role cannot.

#### Setting up the database

These steps are the same on your own server and on a managed service.

1. Sign in as an administrator: on a managed service, the administrator role it gives you. Use that role only for these steps. Never give it to Packmoat, as the owner or as the app role. Take care that its password does not end up in a terminal's history or a log: some providers' command-line tools print it in their default output.
2. Make the two roles and a database of Packmoat's own, owned by the owner role, and let only Packmoat's roles connect to it:

   ```sql
   CREATE ROLE packmoat_owner LOGIN PASSWORD '…';
   CREATE ROLE packmoat_app LOGIN PASSWORD '…';
   CREATE DATABASE packmoat OWNER packmoat_owner;
   REVOKE CONNECT ON DATABASE packmoat FROM PUBLIC;
   GRANT CONNECT ON DATABASE packmoat TO packmoat_app;
   ```

   On some managed services the administrator may not make a database owned by a role it is not a member of, and `CREATE DATABASE … OWNER` fails with "must be able to SET ROLE". Then make the administrator a member of the owner role first, and run `CREATE DATABASE` again:

   ```sql
   GRANT packmoat_owner TO <administrator role>;
   ```

   Give the app role nothing more: it must not be a superuser, and should be a member of no other role. On some managed services the administrator role becomes a member of every role it makes. That lets the administrator act as the app role, not the other way round, so it gives the app role nothing.
3. Check the database's extensions (`\dx` in `psql`, connected to the `packmoat` database). An extension whose functions run as their owner (`SECURITY DEFINER`) is refused at start, because the app role could use them to do what the owner does. `pg_repack` is one: do not install it in Packmoat's database, or revoke its functions from everyone (`REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA repack FROM PUBLIC;`). Never grant the app role `EXECUTE` on a provider's administration functions (for example a function that sets `session_replication_role`), nor on any other extension's functions, whether directly or through a role it is a member of: a superuser's `SECURITY DEFINER` functions run as a superuser, so the start refuses an app role that is granted one, directly or through a role. Never grant one to `PUBLIC` either, not even again after revoking it: the start lets through a superuser's extension functions that everyone may run, because a managed service installs them that way, and it cannot tell a grant to `PUBLIC` from that default. Through such a grant the app role could turn off the database rules.
4. Run the migrations as the owner role, as below.

Then, for a new database and before every upgrade, run the migrations as the owner role, from the image of the version you are moving to:

```
PACKMOAT_DB='Host=…;Database=packmoat;Username=packmoat_owner;Password=…;SSL Mode=VerifyFull' \
  packmoat-admin db migrate --grant-to packmoat_app
```

It runs the database updates, then grants the app role `SELECT`, `INSERT`, `UPDATE` and `DELETE` on the tables (only `SELECT` on the tables that record the schema's version), and the use of their sequences. It grants no `TRUNCATE`, no right to change the schema, and no ownership. All of that is one transaction. It applies the grants on every run, so a table that an update adds gets them too. It then checks the app role the same way the server's start does, and if the role could do more, it refuses and writes nothing. On a platform with jobs (a Container Apps job, a Kubernetes `Job`, an ECS task), run it as a one-off job with the owner's connection string, and give the server only the app role's.

The server never migrates on PostgreSQL. It refuses to start on a schema that is not at its version. So does every `packmoat-admin` command except `db migrate` and `db check-migrations`. `packmoat-admin db check-migrations`, as the owner, runs this version's updates in a transaction that it always rolls back, and reports what they would change: run it before an upgrade.

You can run everything as one role with `db migrate --single-role` and `PACKMOAT_DB_SINGLE_ROLE=on`. The server then logs a warning at every start, because that role can turn off the rules above. A superuser is refused either way.

#### What the app role can still do

The app role cannot change or delete the audit records or the records of deleted organisations, and cannot turn off the rules that keep them. It can still slow or stop the service (denial of service), in three ways that PostgreSQL allows any role with these rights:

- `LOCK TABLE` on a Packmoat table, which makes every other query on that table wait;
- `ALTER ROLE … SET` for itself, which changes its own settings for its next sessions (a different transaction isolation, for example, which then stops the next start);
- holding the lock that database updates take, so `db migrate` waits.

So keep the app role's password as secret as the master key. If you suspect it leaked, change it and restart the server.

### Connections and pooling

- **A direct connection, or a pooler in session mode only.** Packmoat holds locks for a whole session. A pooler in transaction or statement mode (for example PgBouncer with `pool_mode = transaction`, or a provider's "transaction" pool) passes each statement to whichever session is free, and such a lock would not hold. Managed services often give the direct connection and their pools different ports: use the direct one, or a pool you have set to session mode. The start refuses a pooler in transaction mode: it holds two connections open at once, and refuses if they share one database session (an idle pool in transaction mode hands both the same one) or if a lock does not stay with its session. Through a pooler in statement mode the start does not get that far: it stops with the pooler's own error. Still check the mode yourself. `Multiplexing=true` in the connection string is refused for the same reason.
- **Read committed.** Packmoat runs only at PostgreSQL's default transaction isolation, read committed. Do not change `default_transaction_isolation` for the role or the database; the start refuses anything else.
- **Pool size.** The server opens up to `Maximum Pool Size` connections (Npgsql's default is 100). It needs at least 2: the start refuses fewer. It warns, and starts, when the pool is larger than the database lets an ordinary role open (`max_connections`, less the reserved connections). Small managed databases allow few connections, so set `Maximum Pool Size` in the connection string. For example, a small plan with `max_connections` 25, 3 of them reserved, leaves 22: `Maximum Pool Size=10` leaves room for `packmoat-admin` and your own tools. Each `packmoat-admin` command uses its own pool of at most 5 connections, whatever `Maximum Pool Size` says. The start's checks (the server's and each command's) briefly open one more connection, outside the pool, for the pooler check: a role with a `CONNECTION LIMIT` needs room for it, and if the server refuses it as one too many, the start refuses, saying so. The name is `Maximum Pool Size` (and `Minimum Pool Size`): `Max Pool Size` is not accepted. A provider's own pool size does not count here.
- **TLS.** A database that is not on the same machine must be reached over TLS, and the connection string must require it:
  - `SSL Mode=Require` encrypts the connection but does not check whose certificate it is;
  - `SSL Mode=VerifyFull` (best) also checks the server's certificate and name, and `VerifyCA` the certificate only. Both check it against a certificate authority: managed services sign with their own, so download its certificate from your provider, give it to the container as a file, and name it with `Root Certificate=<path to the file in the container>`.
- **`idle_session_timeout`.** Leave it off (0), or set it to at least 5 minutes. A shorter one is refused.

### What the start refuses

The server checks the database before it serves anything, and refuses to start, listing each problem, on:

- a PostgreSQL server older than 17;
- a transaction isolation other than read committed;
- `session_replication_role` other than `origin`;
- a missing or disabled database rule (trigger), or a `search_path` that names no schema that exists;
- an `idle_session_timeout` under 5 minutes (0, off, is fine);
- a schema not at this version;
- a role that is a superuser, may set `session_replication_role`, is a member of a built-in role that reaches past the schema (`pg_execute_server_program`, `pg_read_server_files`, `pg_signal_backend`, `pg_write_all_data`, `pg_write_server_files`), or is granted a superuser's `SECURITY DEFINER` function, directly or through a role (one granted to `PUBLIC` cannot be told from an extension's default, so is not refused: never grant one to `PUBLIC`);
- a role that, without `PACKMOAT_DB_SINGLE_ROLE=on`, owns a table, may create in the schema, holds more on a table than reading and writing rows, or can run a `SECURITY DEFINER` function;
- a pooler in transaction or statement mode;
- a connection to another machine that does not require TLS;
- a `Maximum Pool Size` under 2;
- a database clock more than 30 seconds apart from the server's: copies of the server would disagree on the shared job leases and limits.

It warns, and starts, when the database's clock is more than 2 seconds (and at most 30) apart from the server's, and when `Maximum Pool Size` is larger than the database allows. Every `packmoat-admin` command except the `db` group runs the same checks first, and refuses to run on whatever the server would refuse to start on. The one difference: it does not warn about the pool size, since it uses its own smaller pool.

Managed services have their own administrator roles, such as `rds_superuser`, `azure_pg_admin` and `cloudsqlsuperuser`. These are not PostgreSQL superusers. The start refuses such an administrator role as the server's login, and refuses an app role made a member of its administrator group. Never make the app role a member of one.

### Backups

`packmoat-admin backup`, `verify` and `restore` work on SQLite only, and refuse on PostgreSQL. Use PostgreSQL's own tools instead:

- your provider's automatic backups and point-in-time restore, on a managed service;
- or `pg_dump` and `pg_restore`, run as the owner role, for example `pg_dump --format=custom --file=packmoat.dump` and, into an empty database, `pg_restore --dbname=… packmoat.dump`.

The rest is as for SQLite ([operations.md](operations.md#backups)): encrypt each backup, keep it off the machine, keep the master key apart from it, and practise a restore. A restore needs the same master key. After restoring into a new database, run `db migrate --grant-to` as the owner again, so the app role has its grants there.

There is no tool yet to copy an existing SQLite database into PostgreSQL. Start a PostgreSQL install with a new, empty database.

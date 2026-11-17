# AWS ECS on Fargate

**Status:** Template checked offline in CI; not yet deployed by the project. The server itself is supported (pre-production) on PostgreSQL, more than one copy, and tested in CI against PostgreSQL 17; it has not yet been run on ECS or RDS. The items not yet verified on a real deploy are listed [at the end](#not-yet-verified).

## Why it needs PostgreSQL

A Fargate task gets no disk that survives a restart and is written by one copy only, so Packmoat's default SQLite database cannot live there. On ECS, Packmoat uses a PostgreSQL database instead, with `PACKMOAT_DB_ENGINE=postgres`: an existing RDS for PostgreSQL 17 instance. [PostgreSQL](../self-hosting.md#postgresql) explains how to set one up, and what the server checks before it starts.

## What the template makes

`deploy/aws-ecs/` is an OpenTofu module, pinned to OpenTofu 1.13.9 and the AWS provider 6.73.0. It goes into a VPC you already have, with at least two public subnets (subnets with a route to an internet gateway) in different Availability Zones. With the inputs you give it, it makes:

- **An Application Load Balancer** in the public subnets. HTTPS on 443 with your ACM certificate and the TLS 1.3 policy `ELBSecurityPolicy-TLS13-1-2-2021-06`; HTTP on 80 only answers with a permanent redirect (301) to HTTPS. It drops header fields that are not valid HTTP, uses the strictest desync mitigation mode, appends the client's address to `X-Forwarded-For`, and has deletion protection on.
- **A target group** that sends to each task's own address on 8080 and checks `/readyz`, healthy on 200 only.
- **Two security groups**, each rule a resource of its own. The load balancer's takes 80 and 443 from anywhere and may reach the tasks on 8080 only. The tasks' takes 8080 from the load balancer's group alone, and may reach HTTPS (443) anywhere and PostgreSQL (5432) in `database_cidr`, nothing else.
- **A cluster**, with Container Insights off, and **a log group** `/ecs/<name>`, kept `log_retention_days` days (30 by default).
- **The server's task definition and service:** one task by default (`desired_count`), on Fargate. A deployment starts the new task before it stops the old one (minimum healthy 100%, maximum 200%), and the deployment circuit breaker rolls back to the last task definition that worked if the new one never becomes healthy. ECS Exec is off.
- **A migrate task definition**, run by hand: `packmoat-admin db migrate --grant-to <app role>` from `job_image`, signed in as the owner role. Only this task gets the owner role's connection string. The output `migrate_run_task_command` is the AWS CLI command that runs it once.
- **Two execution roles**, the server task's and the migrate task's. ECS uses an execution role to fetch a task's secrets and to write its logs; each role has one inline policy naming exactly its own secrets' ARNs and the log group's streams, plus `kms:Decrypt` on `secrets_kms_key_arn` through Secrets Manager only, when you give one. No managed policy is attached, and there is **no task role**: the containers' own code gets no AWS credentials. Only ECS tasks in your account may use the roles.
- Only when `enable_nat_gateway` is true: **an Elastic IP, a NAT gateway** in the first public subnet, and **private subnets** (`private_subnet_cidrs`, in `private_subnet_azs`) whose one route out is through it. The tasks then run there (below).

It makes no VPC, no database, no secret and no certificate, and reads nothing from your account while it plans (no data sources): those are steps for you, below. The AWS provider refuses to run against any account but `account_id`, and the template refuses any ARN that is not in `account_id` and `region`.

### How the secrets reach the server

Each secret is a Secrets Manager secret, named by its full ARN. Each task has two containers and a volume of the task's own, empty at each start, at `/run/secrets/packmoat`:

1. **`secrets-init`** runs first, from the same image. It is the only container ECS gives secrets to (as environment variables, fetched by the task's execution role). It runs one fixed script: with `umask 077`, it writes each secret to its own file, `/run/secrets/packmoat/<key>`, makes the file 0400 and owned by user 10001, makes the folder the same user's (0500), and exits. It prints nothing. It runs as root, to hand the files to user 10001, with every one of Fargate's default capabilities dropped but `CHOWN`.
2. **The server** (or the migrate container) starts only once `secrets-init` has exited successfully. It runs as user 10001 with every capability dropped, mounts the volume read-only, and reads each secret through the setting's `_FILE` form.

The files are:

| File | Secret | Read by |
|---|---|---|
| `master_key` | `master_key_secret_arn` | the server (`PACKMOAT_MASTER_KEY_FILE`) |
| `db` | `app_database_secret_arn` | the server (`PACKMOAT_DB_FILE`) |
| `db_ca` | `database_ca_secret_arn` | both, through `Root Certificate` in the connection strings |
| `app_cert` | `app_cert_secret_arn`, optional | the server (`PACKMOAT_APP_CERT`) |
| the setting's name in lower case, such as `packmoat_admin_client_secret` | each `file_settings` entry | the server (`<setting>_FILE`) |
| `db_owner` | `owner_database_secret_arn` | the migrate task only (`PACKMOAT_DB_FILE`) |

No secret is ever an input, an output, or a setting of the server's container. The image sets `PACKMOAT_DB` to its SQLite file, and the server refuses a setting and its `_FILE` both set, so the template starts the server and the migrate command with that one variable unset (`env -u PACKMOAT_DB`).

### Settings

The template sets `PORT=8080` (so the server listens on the task's own address, where the load balancer reaches it), `PACKMOAT_DB_ENGINE=postgres`, `PACKMOAT_COOKIE_KEYS=database`, `PACKMOAT_BACKGROUND_JOBS=on`, `PACKMOAT_HOSTS` and `PACKMOAT_TRUSTED_PROXIES`. Background jobs are on because a Fargate task keeps its CPU between requests; with more than one task, a lease in the database lets one at a time run the daily pass ([Background jobs](../operations.md#background-jobs)). `PACKMOAT_TRUSTED_PROXIES` is `public_subnet_cidrs`: the load balancer reaches the tasks from its own addresses in those subnets. Anything with an address in those subnets may therefore set the `X-Forwarded-*` headers; the tasks' security group is what keeps that to the load balancer, so never admit another source on 8080. Give each network's own address (`10.0.1.0/24`, not `10.0.1.5/24`): the server refuses the other at start, and so does the template. The [settings table](../operations.md#settings) has the rest.

### The network, and what it costs

With no NAT gateway (the default) the tasks run in the public subnets with a public IPv4 address each, so that they can reach the image registry, Secrets Manager, CloudWatch Logs, nuget.org, Azure DevOps and Microsoft Entra. Their security group still admits nothing but the load balancer on 8080. AWS charges for each public IPv4 address by the hour, so each task's address costs a little every month; a NAT gateway costs more.

With `enable_nat_gateway` the tasks run in the template's private subnets with no public address, and everything they send out leaves from one Elastic IP, the output `outbound_ip_address`. Set it only if your customers allowlist the address Packmoat reads their feeds from: the NAT gateway is charged by the hour and per gigabyte.

`deploy/aws-ecs/test-template.sh` checks the module in CI with no AWS account: `tofu fmt`, `tofu init` from the pinned provider (no download), `tofu validate`, `tflint` with the AWS rules, checks on the files' text, and `tofu test`, which plans the module against a stand-in for the AWS provider and checks what it would make and what it refuses. Each check has a change to the module that it must catch.

## Before you deploy

1. **The VPC.** An existing VPC with at least two public subnets in different Availability Zones. Note each subnet's id and IPv4 network, in the same order (`public_subnet_ids`, `public_subnet_cidrs`). With `enable_nat_gateway`, pick two free networks in the VPC for the private subnets, and a zone for each.
2. **The database.** An existing RDS for PostgreSQL 17 instance in the same VPC. Make the database and its two roles as in [Setting up the database](../self-hosting.md#setting-up-the-database), signed in as the master user RDS gives you, and never give that user to Packmoat. In the database's own security group, admit the template's output `task_security_group_id` on 5432 (after the first apply). Write each role's connection string in `Key=Value` form, with the RDS certificate authority as a file:

   ```
   Host=<endpoint>;Database=packmoat;Username=packmoat_app;Password=<password>;SSL Mode=VerifyFull;Root Certificate=/run/secrets/packmoat/db_ca;Maximum Pool Size=20
   ```

   The owner's is the same with the owner role. Set `Maximum Pool Size` so that twice `desired_count` (a deployment runs the new tasks beside the old ones) times it fits the instance's connection limit ([Connections and pooling](../self-hosting.md#connections-and-pooling)). Set `database_cidr` to the instance's subnet, or its address with `/32`.
3. **The secrets,** in Secrets Manager in the same account and region. Store each one from a file, so it never sits in your shell's history:

   ```
   aws secretsmanager create-secret --name packmoat/master-key --secret-string file://master-key
   aws secretsmanager create-secret --name packmoat/db-app --secret-string file://db-app.txt
   aws secretsmanager create-secret --name packmoat/db-owner --secret-string file://db-owner.txt
   aws secretsmanager create-secret --name packmoat/db-ca --secret-string file://<region>-bundle.pem
   ```

   Make the master key with `packmoat-admin keygen`, as in [Kubernetes](kubernetes.md), and keep a copy offline. The CA is the RDS certificate bundle for your region, from AWS's RDS documentation. Delete the local files afterwards. Give the template each secret's **full ARN**, with the six characters Secrets Manager adds to the name (`aws secretsmanager describe-secret --secret-id packmoat/master-key --query ARN`): a name, a partial ARN or an ARN with a JSON key or version is refused. The owner's secret must be its own, never one the server also reads (the template refuses that). If the secrets use your own KMS key, give it as `secrets_kms_key_arn`.
4. **The certificate.** An ACM certificate in the same region covering every name in `hosts` and your admin and customer site hosts, issued (validated) before you apply.
5. **The image.** The template pulls the image anonymously, so it must be in a public registry, such as `ghcr.io/packmoat/packmoat-core`. A private registry (Amazon ECR, among others) is not supported by this template: the execution roles get no permission to pull from one.
6. **Your own rights.** Whoever runs the template needs to manage ECS, Elastic Load Balancing, EC2 security groups (and, with `enable_nat_gateway`, subnets, route tables, Elastic IPs and NAT gateways), IAM roles and their inline policies (and `iam:PassRole` on the two roles), and CloudWatch Logs log groups. No narrower list has been tried on a real deploy.

## Deploy

Copy `deploy/aws-ecs/example.tfvars`, set your values, and keep the state somewhere safe (it holds no secret values; an S3 backend is usual). On a new database, make the migrate task and what it needs first, run it, then apply the rest, so that the service's first tasks find the database at its schema:

```
cd deploy/aws-ecs
tofu init
tofu apply -var-file=packmoat.tfvars -target=aws_ecs_task_definition.migrate -target=aws_ecs_cluster.main \
  -target=aws_vpc_security_group_egress_rule.task_https -target=aws_vpc_security_group_egress_rule.task_database
```

With `enable_nat_gateway`, add `-target=aws_route_table_association.private -target=aws_route.private_nat`. Then admit `task_security_group_id` in the database's security group, and run the command from `tofu output -raw migrate_run_task_command`. Wait for the task to stop, and check that the `migrate` container's exit code is 0 (`aws ecs describe-tasks`, or its log stream under `/ecs/<name>`). Then:

```
tofu apply -var-file=packmoat.tfvars
```

Until the migrate task has run, `/readyz` answers 503, the target group never sees the task as healthy, and the deployment fails. If a task fails to read a secret right after the apply, the new role may not have reached AWS everywhere yet: wait a minute and try again.

The main inputs:

| Input | What it is |
|---|---|
| `account_id`, `region` | Where everything goes. Every ARN you give must be in them. |
| `service_image`, `job_image` | The server's image and the migrate task's, each by digest (`ghcr.io/packmoat/packmoat-core@sha256:<digest>`). A tag is refused. |
| `hosts` | `PACKMOAT_HOSTS`: the feed host names, separated by spaces. |
| `vpc_id`, `public_subnet_ids`, `public_subnet_cidrs` | The existing VPC and its public subnets (at least two, different zones), with their networks in the same order. |
| `database_cidr` | The database's network: the tasks may reach PostgreSQL there, and nowhere else. |
| `certificate_arn` | The ACM certificate for the HTTPS listener. |
| `master_key_secret_arn`, `app_database_secret_arn`, `owner_database_secret_arn`, `database_ca_secret_arn` | The secrets' full ARNs. The owner's may not be one the server also reads. |
| `app_role_name` | The app role the migrate task grants to. Default: `packmoat_app`. |
| `app_cert_secret_arn` | Optional: the customer site's certificate and key (`PACKMOAT_APP_CERT`). |
| `secrets_kms_key_arn` | Optional: your own KMS key, if the secrets use one. |
| `file_settings` | Optional secret settings, read from Secrets Manager through their `_FILE` form, such as `{ PACKMOAT_ADMIN_CLIENT_SECRET = "arn:aws:secretsmanager:..." }`. |
| `settings` | Other settings that are not secret, such as `{ PACKMOAT_ADMIN_HOST = "admin.example.com" }`. It takes `PACKMOAT_` settings only; a secret setting, a `_FILE` setting, one the template sets itself, or any `PACKMOAT_DB` setting (`PACKMOAT_DB_SINGLE_ROLE` among them) is refused. `file_settings` refuses the same, and the master key. |
| `desired_count` | Default 1, at most 10. More than one task is supported on PostgreSQL. |
| `cpu`, `memory` | The server task's size: default 512 CPU units and 1024 MiB. Fargate allows only some pairs. |
| `log_retention_days` | Default 30. |
| `enable_nat_gateway`, `private_subnet_cidrs`, `private_subnet_azs` | Default false. See above. |

**More than one task.** As on any platform, every copy keeps its forms, its limits for signed-in actions, the cookie keys and the job leases in the database, so no sticky sessions are needed; budget N × `Maximum Pool Size` connections, and remember that limits on traffic nobody has signed in to are counted by each copy on its own ([More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql)).

**Your host names.** The output `load_balancer_dns_name` is the load balancer's own name. Point each of your names at it: a Route 53 alias record (with `load_balancer_zone_id`), or a CNAME. Packmoat answers only the names in `hosts` and your site hosts.

**Removing it.** The load balancer has deletion protection on, so `tofu destroy` fails on it until you set `enable_deletion_protection` to false in your copy and apply.

## Upgrade

The migrate task and the server each have their own image input, so the database moves first:

1. Take an RDS snapshot, or note the time for point-in-time restore.
2. Set `job_image` to the new release's digest and apply. Only the migrate task definition changes (a new revision).
3. Run `tofu output -raw migrate_run_task_command`'s command, wait for the task to stop, and check the `migrate` container's exit code is 0.
4. Set `service_image` to the same digest and apply. The service starts a task from the new revision, moves the traffic to it once `/readyz` answers 200, and then stops the old one.

If step 3 is skipped or fails, the new task never becomes healthy, the circuit breaker rolls the service back, and the old task keeps serving. Upgrade one release at a time, as [More than one copy, on PostgreSQL](../operations.md#more-than-one-copy-on-postgresql) explains.

## What it costs

What the template makes, and what each part costs; AWS's pricing pages have the rates.

- **Fargate:** each task, by the second, for its CPU and memory, while it runs; the migrate task only while it runs.
- **The Application Load Balancer:** by the hour, and by use (load balancer capacity units).
- **Public IPv4 addresses:** by the hour, for each task's address with no NAT gateway, and for the load balancer's own addresses.
- **`enable_nat_gateway`:** the NAT gateway by the hour and per gigabyte, and its Elastic IP.
- **CloudWatch Logs:** per gigabyte written and stored. Container Insights is off, as it adds charges of its own.
- **Secrets Manager:** per secret per month, and per 10,000 reads.
- **RDS** is your own instance, and its cost is separate.

## Backups

`packmoat-admin backup` works on SQLite only. Use RDS's own snapshots and point-in-time restore, or `pg_dump`, and keep the master key apart from them. A restore needs the same master key.

## Not yet verified

The project has not deployed this template. Until a real deploy settles them, these are assumptions:

- **The health check's `Host` header.** The template sets none. The server answers `/readyz` when the `Host` is an IP address, `localhost` or one of its names ([Health checks](../operations.md#health-checks)); that the load balancer's health check sends the task's address as the `Host` is not measured.
- **`X-Forwarded-Host`.** The load balancer probably passes on a client's own `X-Forwarded-Host` rather than overwriting it. With the load balancer trusted, a client's `X-Forwarded-Host` is then believed; Packmoat uses a forwarded host name only if it is one it serves, so a client can pick among your own names, and no other. `X-Forwarded-For` is set to `append`, so the last address is the one the load balancer saw; that the server picks the client's address from it as expected is not measured.
- **The bind volume.** That a Fargate task's own volume starts empty, is writable by root in `secrets-init`, and lets root hand files and the folder to user 10001 with only `CHOWN` left.
- **`dependsOn` with `SUCCESS`:** that the server's container waits for `secrets-init` to exit 0, and the task stops if it does not.
- **Dropping capabilities:** that Fargate accepts `drop = ["ALL"]` for the server and the list of every default capability but `CHOWN` for `secrets-init`, and that the server runs with none.
- **The RDS certificate bundle:** that a region's bundle fits in one secret (Secrets Manager allows 64 KB) and that `SSL Mode=VerifyFull` with `Root Certificate` set to it connects.
- **`env -u PACKMOAT_DB`:** that the image's `/usr/bin/env` unsets it before the server and `packmoat-admin` start.
- **`PORT=8080`:** that the server then listens on the task's address, where the load balancer reaches it.
- **Pulling the image through a public address:** that a task in a public subnet with a public IPv4 address reaches `ghcr.io`, Secrets Manager and CloudWatch Logs with no NAT gateway and no VPC endpoints, through HTTPS alone.
- **The deployment circuit breaker:** how long it takes to roll back a deployment whose task never becomes healthy, and what a first deployment, with nothing to roll back to, does.
- **The TLS policy name** `ELBSecurityPolicy-TLS13-1-2-2021-06`: that the listener accepts it in your region.
- **The first apply's targets:** that the targeted apply above makes everything the migrate task needs, and that `tofu output` shows the command after it.
- **What AWS accepts:** the checks run with OpenTofu 1.13.9, the AWS provider 6.73.0, tflint 0.66.0 and its AWS ruleset 0.50.0, against a stand-in for the provider. They check what the module plans, not what AWS's APIs accept: names, sizes and the deployer rights above have not been tried.
- **Costs:** the parts listed above are from AWS's pricing pages, not from a bill.

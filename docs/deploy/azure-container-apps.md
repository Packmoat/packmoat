# Azure Container Apps: not supported yet

Packmoat does not run on Azure Container Apps yet.

## Why

Packmoat keeps its data in SQLite: one database file, on a local disk that survives restarts, written by exactly one running copy of the server. Container Apps gives a container no such disk: its persistent storage is a network file share (Azure Files), and a SQLite database is not safe on a network file share. Its replicas can be capped at 1, but that alone does not fix the storage. So Packmoat needs a database server there, such as Postgres, and Packmoat does not support one yet. The setting `PACKMOAT_DB_ENGINE=postgres` is reserved for it; today it stops the server with an error.

## What it will look like

Once Packmoat supports Postgres, a Container Apps deployment will be the same image, with:

- Container Apps' own HTTPS ingress in front, so `PORT` and `PACKMOAT_TRUSTED_PROXIES` are set as for [your own proxy](own-proxy.md);
- a Postgres database instead of the SQLite file;
- the master key and other secrets given as files, through each setting's `_FILE` form.

Until then, use [one server with Docker](single-server.md) or [Kubernetes](kubernetes.md). [README.md](README.md) lists the ways to run Packmoat today.

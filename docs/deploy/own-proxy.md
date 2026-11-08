# Running Packmoat behind your own proxy

Use this when you already have something that handles HTTPS in front of your services: nginx, a cloud load balancer or similar. Packmoat itself speaks plain HTTP. [README.md](README.md) lists what every deployment needs; this page covers only the proxy.

## What the proxy must do

- **Handle HTTPS** for every Packmoat host name: the feed names in `PACKMOAT_HOSTS`, and the admin and customer site names if you use them.
- **Pass on the original `Host` header.** Packmoat answers only the host names it is configured for, and `localhost` and `127.0.0.1`. Any other name gets 400.
- **Set `X-Forwarded-For` to the client's address**, replacing any value the client sent, never adding to it. Packmoat limits wrong tokens per client address: without the real address every client looks like the proxy, and one client's wrong tokens slow down everyone.
- **Set `X-Forwarded-Proto` to `https`**, so the addresses Packmoat writes in its answers start with `https://`.
- **Set `X-Forwarded-Host` to the original host name**, or remove it. Never pass on a value the client sent.
- **Forward requests to Packmoat's port.** That is `127.0.0.1:8080` when the proxy runs on the same machine and `PORT` is unset, or the `PORT` you set.

## `PORT` and `PACKMOAT_TRUSTED_PROXIES`

With `PORT` unset, Packmoat listens on `127.0.0.1:8080` only, and believes forwarded headers only from the same machine (loopback). That is right when the proxy runs on the same machine and shares its network, as in the [single-server kit](single-server.md).

When the proxy reaches Packmoat over a network (from another machine, or from another container), set both:

- `PORT`: the port Packmoat listens on, on every address. For example `8080`.
- `PACKMOAT_TRUSTED_PROXIES`: the addresses or networks the proxy's connections come from, as Packmoat sees them, separated by spaces. Only these may tell Packmoat the client's address. Never `0.0.0.0/0` or `::/0`, which would let any client choose its address; Packmoat refuses them.

Then make sure nothing but the proxy can reach that port: anything that can reach it can skip HTTPS. The [settings table](../operations.md#settings) describes both settings in full.

## A minimal nginx example

One feed host name, with Packmoat on another machine at `10.0.0.5`, `PORT=8080` and `PACKMOAT_TRUSTED_PROXIES` set to the nginx machine's address. The certificate paths are yours to fill in.

```
server {
    listen 443 ssl;
    server_name packages.contoso.example;

    ssl_certificate     /etc/nginx/tls/packages.contoso.example.crt;
    ssl_certificate_key /etc/nginx/tls/packages.contoso.example.key;

    location / {
        proxy_pass http://10.0.0.5:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header X-Forwarded-Host $host;
    }
}
```

`$remote_addr` is the address of whoever connected to nginx, so it replaces anything the client sent. (`$proxy_add_x_forwarded_for` would add to the client's own header instead.) Add a `server` block like it for the admin and customer site names if you use them.

Check it from outside: `curl https://packages.contoso.example/healthz` answers `ok` and the build's version. [Health checks](../operations.md#health-checks) covers `/readyz` too.

# Concepts

The words Packmoat uses, in plain terms. [spec/README.md](../spec/README.md) gives the exact rules, with test cases. [architecture.md](architecture.md) shows how the parts fit together.

## Organisations

One Packmoat server serves many organisations. Each one has its own feed, its own settings (cooldown, per-package cooldowns, internal prefixes and waivers), its own tokens and its own audit trail. One organisation never sees another's. The code and the operator command call an organisation a **tenant**.

Each organisation brings its own package feed in Azure Artifacts, which holds packages from nuget.org. Packmoat reads that feed on the organisation's behalf and passes on only what its rules allow.

## Cooldown

The **cooldown** is how many days a package version must have been public on nuget.org before Packmoat serves it. The default is 14 days. A version exactly as old as the cooldown is served.

A version that is still too young is **hidden**: it is left out of the version lists NuGet reads, and a request for its file is answered "not found". NuGet then picks the newest version that is old enough, without any error. A project that asks for one exact version that is hidden fails to restore instead, until the version is old enough or waived.

A cooldown of 0 days turns the cooldown off: every version is served, unless an internal prefix hides it.

**Per-package cooldowns.** An organisation can give one package its own number of days, longer or shorter than its usual cooldown. That number replaces the usual cooldown for that package only.

## Waivers

A **waiver** lets a version through before its cooldown is over, for a limited time. Use one when you need a fix now and cannot wait. A waiver ends by itself on its end date, and can be revoked sooner. Every waiver records who added it and why.

- An organisation's own admins can waive one exact version, for 1 to 30 days, with a reason. An organisation can have at most 25 of these active at once.
- The operator can also waive every version of a package at once, and for longer.

A waiver never lets through a package that an internal prefix hides.

## Internal prefixes

An **internal prefix** is the start of the names of your own private packages, such as `Contoso.`. A public package whose name starts with one of your internal prefixes is never served, whatever else is set: no waiver and no cooldown lets it through. This stops **dependency confusion**: someone publishing a public package with the name of one of yours, hoping a build picks it up.

The match is on the start of the name, in any case. `Contoso.` covers `Contoso.Json` and `contoso.web`; `Contoso` without the dot also covers `ContosoTools`.

## Tokens

Developers and build agents give NuGet a **token** to use the feed. Each token belongs to one organisation, and reaches only that organisation's feed.

- A token is shown once, when it is made. Packmoat keeps only a fingerprint of it (a hash), so nobody can read it back later, not even the operator.
- A token can be revoked at any time. It stops working within seconds.
- The operator can make tokens. If the operator runs the customer site, an organisation's own admins can make and revoke theirs too.
- The developer's token stops at Packmoat. Packmoat reads the organisation's feed with its own identity, and never passes that on.

## Fail closed

When Packmoat cannot be sure a version is safe to serve, it does not serve it. This is called **failing closed**.

- A version whose publish date is unknown is hidden (unless the cooldown is 0).
- If a rule cannot decide, the version is hidden.
- If nuget.org cannot be reached and Packmoat has no recent answer, the request fails, and NuGet can try again later.
- A request Packmoat does not recognise is answered "not found".

A restore may fail for a while. It never gets a version that the rules did not allow.

## Where publish dates come from

Publish dates come from **nuget.org**, never from the organisation's feed. The feed's own date is when the feed saved a copy of the package, which says nothing about how long the version has been public.

- Packmoat asks nuget.org for a package's dates when a request needs them, and keeps the answer in memory. It uses that answer for 10 minutes, then asks again.
- If nuget.org cannot be reached, Packmoat keeps using an answer up to 24 hours old. Past that, it fails closed.
- The dates are not saved anywhere else. After a restart, Packmoat asks nuget.org again.
- A version nuget.org does not list, or lists without a real date (as for an unlisted version), has no known publish date, so it is hidden.

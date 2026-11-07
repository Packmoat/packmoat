# Contributing to Packmoat

Thank you for helping. Pull requests from outside the project are welcome.

## Scope

This repository is the open-source core: the server, the operator command, the policy engine, the protocol adapter, the deploy kit, the conformance spec and their docs. Changes to the core are in scope; nothing else is.

## Before you start

For a small fix, open a pull request. For a large change (a new feature, a new dependency, a change to how the parts fit together or to a stored format), open an issue first. Say what you want to change and why, so we can agree on the approach before you spend time on it.

## How to work

- Write the test first. Show that it fails without your change, then make it pass.
- Run every test before you open a pull request:

  ```
  cd dotnet
  dotnet test
  ```

- Keep a pull request to one change. Say in its description what it changes and how you tested it.

## Sign your commits (DCO)

Every commit needs a Developer Certificate of Origin (DCO) sign-off. By signing off, you certify that you wrote the change or otherwise have the right to submit it under the project's licence, as set out at https://developercertificate.org.

Sign off with `git commit -s`. Git adds a line like this to the commit message, from your `user.name` and `user.email`:

```
Signed-off-by: Ada Lovelace <ada@contoso.example>
```

Use your real name. A pull request with a commit that is not signed off is not merged. To sign off commits you have already made, run `git rebase --signoff main` and push again.

## Licence

By contributing, you agree that your contributions are licensed under the Apache License, Version 2.0 (see [LICENSE](LICENSE)).

## Security

Do not report a vulnerability in an issue or a pull request. See [SECURITY.md](SECURITY.md).

## Conduct

Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).

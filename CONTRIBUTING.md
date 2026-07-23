# Contributing to Shoddy

Thanks for your interest in improving the Shoddy language, its machines
(standard library), and the VS Code extension. Contributions of all
sizes — bug reports, docs, tests, machines, and compiler work — are
welcome.

## License of contributions

Shoddy is released under the [MIT License](LICENSE). By submitting a
contribution, you agree that your contribution is licensed under the same
MIT License, and you certify that you have the right to submit it under
that license (see the [Developer Certificate of Origin](https://developercertificate.org/)).

## Contributor License Agreement

This project intends to join the [.NET Foundation](https://dotnetfoundation.org/).
Contributors will be asked to sign the **.NET Foundation Contributor
License Agreement (CLA)** the first time they open a pull request; the
CLA-assistant bot comments on the PR with a one-click signing link. You
only sign once, and it covers all future contributions to .NET Foundation
projects.

## How to contribute

1. Open an issue to discuss anything non-trivial before you start.
2. Fork, branch, and make your change.
3. Build and run the tests locally:
   ```
   dotnet publish src/Shoddy.Mill -c Release -o bin
   dotnet test src/Shoddy.Tests        # golden conformance suite
   bin/mill run tst/libtest.shoddy      # ends: ALL ASSERTIONS PASSED
   ```
4. Keep the golden fixtures (`tst/golden/`) authoritative — if your change
   alters output intentionally, update the fixtures in the same PR and
   explain why.
5. Open a pull request describing the change and the motivation.

## Code of Conduct

This project has adopted a [Code of Conduct](CODE_OF_CONDUCT.md). By
participating, you are expected to uphold it.

# Alvo

> **Alvo** · *Application Layer for Vision & Operations* · "Your intent, running in production."

Alvo is a .NET-native Backend-as-a-Service framework for the agentic age, distributed as the
`MMLib.Alvo.*` NuGet package family. It runs standalone (Docker) or embedded in an existing
ASP.NET Core host — same code, two distributions.

The full delivery strategy and technical spec live in
[`docs/product/alvo-specifikacia.md`](docs/product/alvo-specifikacia.md); the domain analysis
behind it is in [`docs/product/baas-analyza.md`](docs/product/baas-analyza.md).

## Building & testing

Requires the .NET SDK pinned in [`global.json`](global.json) (`10.0.100`).

```bash
dotnet build
dotnet test
```

Tests run on **Microsoft.Testing.Platform (MTP)**, not VSTest (see the `test` section in
`global.json`).

## Packages

Alvo ships as a family of focused NuGet packages, added as they're earned rather than assumed
up front — see [`docs/architecture/package-boundary.md`](docs/architecture/package-boundary.md)
for the rule and the current list. Today that list is:

| Package | Description |
| --- | --- |
| `MMLib.Alvo.Abstractions` | The interface-first root of the dependency graph — no source yet, ports/interfaces land in a later phase. |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the build/test workflow, coding conventions, and
the pull request process (including the CLA).

## License

Apache-2.0 — see [`LICENSE`](LICENSE).

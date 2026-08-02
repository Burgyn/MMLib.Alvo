# Alvo

> **Alvo** · *Application Layer for Vision & Operations* · "Your intent, running in production."

Alvo is a .NET-native Backend-as-a-Service framework for the agentic age, distributed as the
`MMLib.Alvo.*` NuGet package family. It runs standalone (Docker) or embedded in an existing
ASP.NET Core host — same code, two distributions.

The full delivery strategy and technical spec live in
[`docs/product/alvo-specifikacia.md`](docs/product/alvo-specifikacia.md); the domain analysis
behind it is in [`docs/product/baas-analyza.md`](docs/product/baas-analyza.md).

## Run the demo backend (standalone)

```bash
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up --build --wait --wait-timeout 60
curl -sS http://localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
```

The backend is defined entirely by [`examples/vehicle-registry/vehicles.alvo.json`](examples/vehicle-registry/vehicles.alvo.json),
mounted into the container — no code, no migrations, no clicking. Interactive docs:
<http://localhost:8080/scalar>.

The image ships no credential, so `ALVO_DEMO_KEY_SECRET` is required rather than defaulted: the stack refuses
to start without it. Every compose command interpolates the file, `docker compose down` included, so keep the
variable exported (or put it in a root `.env`) for the whole session. Tear down with
`docker compose down --volumes`.

### The complex demo

`vehicle-registry` is deliberately small — three entities, no tenancy, no `audit`, no hidden fields.
For the one that exercises the whole feature surface, see
[`examples/field-service`](examples/field-service/README.md): two tenants, five keys differing only
in role and tenant, an audited entity beside an unaudited one, hidden and `readOnly` fields, and
rules that differ by role. It runs on `:8081` from its own compose file, and
[`test/teapie-field-service`](test/teapie-field-service/README.md) drives it end to end.
`scripts/test-e2e` runs both stacks and both suites — the same thing CI runs.

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

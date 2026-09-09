# API contract linting (Vacuum) + API invariant tests — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #26 — lint the generated OpenAPI document against a committed Spectral-compatible ruleset, and prove default-deny, idempotency and a consistent CRUD shape hold across 16 generated descriptors plus the four shipped examples, with both halves proven non-vacuous.

**Architecture:** One ruleset file (`schema/openapi-ruleset.yaml`) is the only authority on static shape; `vacuum` is invoked as a child process by two callers — `scripts/lint-api` (cheap, no build) and the new slow-tier test project (per generated document, parsing `spectral-report`'s JSON for rule-id attribution). Everything conditional or cross-referential is a C# fact in `test/_shared/api/OpenApiDocumentFacts.cs`, applied identically to the fixture, the examples and the generated documents.

**Tech Stack:** vacuum v0.30.3 (Go binary, MIT, dev-tool carve-out), xunit.v3 4.0.0 on Microsoft.Testing.Platform, CsCheck 4.8.0, Shouldly, SQLite, bash.

**Spec:** `docs/superpowers/specs/2026-09-08-f4-api-invariants-vacuum-design.md`

## Global Constraints

- **Vacuum is pinned to v0.30.3** and verified against the release's own `checksums.txt`. Never `latest`.
- **No Docker for vacuum**: CI runs ring2 on `windows-latest`, whose Docker is in Windows-container mode. Asset names are `vacuum_0.30.3_{darwin_arm64,darwin_x86_64,linux_x86_64,linux_arm64,windows_x86_64}.tar.gz`; the Windows tarball carries `vacuum.exe`, the others `vacuum`.
- **The gate runs at `--fail-severity warn`.** After the mutes and the §3.1 fix the document is measured clean at every severity.
- **Rules see the resolved document.** Never write a rule asserting a `$ref` is present.
- **`field: "@key"` works with `casing`, and is silently vacuous with `pattern`** — measured. Never use `pattern` with `@key`.
- **No JSONPath filter expressions** (`[?(...)]`) — they fail to parse and one bad `given` aborts the whole run.
- **Rule ids come from `vacuum spectral-report -r <ruleset> <doc> <out>`**, whose JSON array carries `code`, `severity` (0 = error, 1 = warn), `message`, `path`. `vacuum lint`'s console output prints descriptions, not ids.
- **New test projects must be named `*.Tests.Integration.csproj`** to land in the slow tier, and need an `InternalsVisibleTo` entry in `src/MMLib.Alvo/Properties/AssemblyInfo.cs`.
- **C# files are written with CRLF + BOM** (the repo's `dotnet format` pre-commit gate rejects otherwise). Write them with the Write tool, not with bash heredocs.
- **Never commit to `main`.** Branch is `feat/26-api-invariants-vacuum`.

---

## File Structure

| File | Responsibility |
|---|---|
| `scripts/ensure-vacuum` | Resolve a pinned, checksum-verified vacuum binary into `artifacts/tools/vacuum/`; print its path on stdout |
| `scripts/test-ensure-vacuum` | Hermetic suite for the above: asset mapping, checksum refusal, reuse of a present binary |
| `scripts/lint-api` | Lint the committed document snapshot with the ruleset — no .NET, no build |
| `schema/openapi-ruleset.yaml` | The one authority on static document shape |
| `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` | +`Origin()`: normalise a `servers[*].url` whose path is bare `/` |
| `test/_shared/api/OpenApiDocumentFacts.cs` | Generic document assertions over `(document, entities)` |
| `test/_shared/api/AlvoApiWorld.cs` | +`FromDescriptorPathAsync(absolutePath, …)` |
| `test/MMLib.Alvo.Api.Invariants.Tests.Integration/` | The new slow-tier project: vacuum runner, lint battery, generator, invariants, sabotage |
| `scripts/test-ring2` | Placeholder → `ensure-vacuum` + `lint-api` |

---

### Task 1: `scripts/ensure-vacuum` and its suite

**Files:**
- Create: `scripts/ensure-vacuum`
- Create: `scripts/test-ensure-vacuum`
- Modify: `.github/workflows/ci.yml` (the `scripts` job — add the new suite next to the hook/mutation/load suites)

**Interfaces:**
- Consumes: nothing.
- Produces: `scripts/ensure-vacuum` prints the absolute path of a runnable vacuum binary on stdout and exits 0; exits non-zero with a message on any failure. Honours `ALVO_VACUUM_BASE_URL` (default `https://github.com/daveshanley/vacuum/releases/download`) so the suite can point it at a local directory, and `ALVO_VACUUM_HOME` (default `<repo>/artifacts/tools/vacuum`).

- [ ] **Step 1: Write the failing suite**

Create `scripts/test-ensure-vacuum` — hermetic, no network. It builds a fake release directory (a tarball containing a `vacuum` shell script that echoes the pinned version, plus a `checksums.txt`), points `ALVO_VACUUM_BASE_URL` at it via `file://`, and asserts:

```bash
#!/usr/bin/env bash
# test-ensure-vacuum — the suite for scripts/ensure-vacuum.
#
# Hermetic: no network. A fake release directory is built in a temp dir and served over file://,
# which is what ALVO_VACUUM_BASE_URL exists for. The checksum check is a security control, so the
# case that matters most is the REFUSAL — a verification that silently passes is the whole reason
# this suite exists.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DIR/.." && pwd)"
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok: $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL: $1" >&2; }

VERSION="$(grep -m1 '^VERSION=' "$DIR/ensure-vacuum" | cut -d= -f2 | tr -d '"')"
[ -n "$VERSION" ] || { echo "cannot read VERSION from ensure-vacuum" >&2; exit 1; }

fake_release() {         # $1 = dir to build the release in; $2 = asset base name
  local dir="$1" asset="$2" stage
  stage="$(mktemp -d)"
  printf '#!/usr/bin/env bash\necho %s\n' "$VERSION" > "$stage/vacuum"
  chmod +x "$stage/vacuum"
  mkdir -p "$dir/v$VERSION"
  tar czf "$dir/v$VERSION/$asset" -C "$stage" vacuum
  ( cd "$dir/v$VERSION" && shasum -a 256 "$asset" > checksums.txt )
}

# 1. a good release resolves to a runnable binary
work="$(mktemp -d)"; rel="$work/release"
asset="$("$DIR/ensure-vacuum" --print-asset)"
fake_release "$rel" "$asset"
path="$(ALVO_VACUUM_BASE_URL="file://$rel" ALVO_VACUUM_HOME="$work/home" "$DIR/ensure-vacuum")"
if [ -x "$path" ] && [ "$("$path" version)" = "$VERSION" ]; then ok "resolves a pinned binary"; else bad "did not resolve a runnable binary (got '$path')"; fi

# 2. a tampered asset is REFUSED, and nothing is left behind
work2="$(mktemp -d)"; rel2="$work2/release"
fake_release "$rel2" "$asset"
printf 'tampered' >> "$rel2/v$VERSION/$asset"
if ALVO_VACUUM_BASE_URL="file://$rel2" ALVO_VACUUM_HOME="$work2/home" "$DIR/ensure-vacuum" >/dev/null 2>&1; then
  bad "a checksum mismatch was accepted"
else
  ok "a checksum mismatch is refused"
fi
if [ -e "$work2/home/vacuum" ] || [ -e "$work2/home/vacuum.exe" ]; then bad "a refused download was left in place"; else ok "a refused download leaves nothing behind"; fi

# 3. an already-resolved binary is reused, with the release directory GONE
work3="$(mktemp -d)"; rel3="$work3/release"
fake_release "$rel3" "$asset"
ALVO_VACUUM_BASE_URL="file://$rel3" ALVO_VACUUM_HOME="$work3/home" "$DIR/ensure-vacuum" >/dev/null
rm -rf "$rel3"
if ALVO_VACUUM_BASE_URL="file://$rel3" ALVO_VACUUM_HOME="$work3/home" "$DIR/ensure-vacuum" >/dev/null 2>&1; then
  ok "a resolved binary is reused without downloading"
else
  bad "re-resolution tried to download again"
fi

echo "[test-ensure-vacuum] $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
```

- [ ] **Step 2: Run it to verify it fails**

Run: `scripts/test-ensure-vacuum`
Expected: FAIL — `scripts/ensure-vacuum` does not exist (`No such file or directory`).

- [ ] **Step 3: Write `scripts/ensure-vacuum`**

```bash
#!/usr/bin/env bash
# ensure-vacuum — resolve the pinned Vacuum binary and print its path.
#
# Vacuum lints the generated OpenAPI document (schema/openapi-ruleset.yaml). It is a dev/CI tool
# invoked as a separate process, which is the carve-out alvo-dotnet-conventions already names it in:
# nothing ships, nothing is linked.
#
# WHY A DOWNLOAD AND NOT A DOCKER IMAGE. CI runs scripts/test-ring2 on windows-latest, whose Docker
# daemon is in Windows-container mode and cannot run a Linux image. A vacuum that only existed in a
# Linux container would make the invariant suite self-skip there — and CI sets
# TESTINGPLATFORM_EXITCODE_IGNORE=8 on Windows, so a suite that ran zero tests would pass. A pinned,
# checksum-verified release asset exists for every platform the matrix has (measured), so this is the
# only shape that keeps the gate honest on both.
#
# The version is pinned because a linter's ruleset semantics are part of the gate: a minor release
# that adds a recommended rule would turn a green PR red for a reason nobody changed. Bump it
# deliberately, and re-run scripts/test-ring2 when you do.
set -euo pipefail
VERSION="0.30.3"
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DIR/.." && pwd)"
BASE_URL="${ALVO_VACUUM_BASE_URL:-https://github.com/daveshanley/vacuum/releases/download}"
HOME_DIR="${ALVO_VACUUM_HOME:-$ROOT/artifacts/tools/vacuum}"

asset_name() {
  local os arch
  case "$(uname -s)" in
    Darwin)             os=darwin ;;
    Linux)              os=linux ;;
    MINGW*|MSYS*|CYGWIN*) os=windows ;;
    *) echo "ensure-vacuum: unsupported OS '$(uname -s)'" >&2; return 1 ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64)  arch=x86_64 ;;
    *) echo "ensure-vacuum: unsupported architecture '$(uname -m)'" >&2; return 1 ;;
  esac
  echo "vacuum_${VERSION}_${os}_${arch}.tar.gz"
}

binary_name() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) echo "vacuum.exe" ;; *) echo "vacuum" ;; esac; }

if [ "${1:-}" = "--print-asset" ]; then asset_name; exit 0; fi
if [ "${1:-}" = "--print-version" ]; then echo "$VERSION"; exit 0; fi

resolved="$HOME_DIR/$(binary_name)"
if [ -x "$resolved" ] && [ "$("$resolved" version 2>/dev/null || true)" = "$VERSION" ]; then
  echo "$resolved"; exit 0
fi

# A local vacuum at the pinned version is used as-is (the k6 precedent in scripts/test-load); a
# different local version warns rather than being silently trusted, because the ruleset is versioned
# against this one.
if command -v vacuum >/dev/null 2>&1; then
  local_version="$(vacuum version 2>/dev/null | tr -d '\r' || true)"
  if [ "$local_version" = "$VERSION" ]; then command -v vacuum; exit 0; fi
  echo "ensure-vacuum: WARNING local vacuum is '$local_version', not $VERSION — downloading the pinned one" >&2
fi

asset="$(asset_name)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
echo "ensure-vacuum: fetching $asset" >&2
curl -sSLf -o "$stage/$asset" "$BASE_URL/v$VERSION/$asset"
curl -sSLf -o "$stage/checksums.txt" "$BASE_URL/v$VERSION/checksums.txt"

expected="$(grep " $asset\$" "$stage/checksums.txt" | awk '{print $1}')"
if [ -z "$expected" ]; then echo "ensure-vacuum: $asset is not listed in checksums.txt" >&2; exit 1; fi
if command -v shasum >/dev/null 2>&1; then actual="$(shasum -a 256 "$stage/$asset" | awk '{print $1}')";
else actual="$(sha256sum "$stage/$asset" | awk '{print $1}')"; fi
if [ "$expected" != "$actual" ]; then
  echo "ensure-vacuum: checksum mismatch for $asset (expected $expected, got $actual)" >&2
  exit 1
fi

tar xzf "$stage/$asset" -C "$stage"
mkdir -p "$HOME_DIR"
mv "$stage/$(binary_name)" "$resolved"
chmod +x "$resolved"
echo "$resolved"
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `chmod +x scripts/ensure-vacuum scripts/test-ensure-vacuum && scripts/test-ensure-vacuum`
Expected: `3 passed, 0 failed` (four `ok:` lines — case 2 asserts twice).

- [ ] **Step 5: Verify the real download works too**

Run: `scripts/ensure-vacuum && "$(scripts/ensure-vacuum)" version`
Expected: the path, then `0.30.3`.

- [ ] **Step 6: Add the suite to CI's `scripts` job**

In `.github/workflows/ci.yml`, after the `Load-baseline guard suite` step, add:

```yaml
      - name: Vacuum resolver suite
        run: scripts/test-ensure-vacuum
```

- [ ] **Step 7: Commit**

```bash
git add scripts/ensure-vacuum scripts/test-ensure-vacuum .github/workflows/ci.yml
git commit -m "build: pin and verify the Vacuum binary (#26)"
```

---

### Task 2: The document advertises a server URL a client can append to

**Files:**
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: `servers[0].url` never ends in `/`. Task 3's ruleset keeps `oas3-api-servers` at its recommended severity because of this.

- [ ] **Step 1: Write the failing test**

In `OpenApiServersTests`, replace `With_no_path_base_the_document_advertises_the_bare_origin` with the value the spec requires, and say why in the summary:

```csharp
    /// <summary>
    /// With no path base the origin is the bare origin — and <b>without a trailing slash</b>.
    /// </summary>
    /// <remarks>
    /// OpenAPI 3.1.1 §4.8.5 is explicit that a path key is <em>appended</em> to this value, "no relative URL
    /// resolution", so <c>http://localhost/</c> makes a conforming client build
    /// <c>http://localhost//api/products</c>. This pinned the slash-suffixed value until #26's Vacuum lint
    /// reported it (<c>oas3-api-servers</c>); the path-base case below was always correct, so the fix makes
    /// the two consistent rather than special-casing either.
    /// </remarks>
    [Fact]
    public async Task With_no_path_base_the_document_advertises_the_bare_origin()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var origin = await OriginAsync(world, "/openapi/v1.json");

        origin.ShouldBe("http://localhost");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-method '*With_no_path_base*'`
Expected: FAIL — actual `http://localhost/`.

- [ ] **Step 3: Normalise the origin in the transformer**

In `AlvoDocumentTransformer.TransformAsync`, after the `generated.Count == 0` early return and before `Overview(document)`, add `Origin(document);` and implement it beside `Overview`:

```csharp
    /// <summary>Drops a bare trailing slash from every advertised server URL.</summary>
    /// <remarks>
    /// OpenAPI 3.1.1 §4.8.5: a path key is <em>appended</em> to this value, with "no relative URL
    /// resolution". A host served at the root gets <c>http://localhost/</c> from ASP.NET's own server
    /// derivation, and appending <c>/api/products</c> to that yields a double slash — a URL that is wrong
    /// by the specification's own construction rule, which #26's Vacuum lint reports as
    /// <c>oas3-api-servers</c>. Only the bare-root case is touched: a URL carrying a real path
    /// (<c>http://localhost/alvo</c>, the path-base shape #130 pins) already has no trailing slash, and a
    /// server the host declared for itself is not rewritten beyond this.
    /// </remarks>
    private static void Origin(OpenApiDocument document)
    {
        if (document.Servers is null)
        {
            return;
        }

        foreach (var server in document.Servers)
        {
            if (server.Url is { Length: > 1 } url && url.EndsWith('/'))
            {
                server.Url = url.TrimEnd('/');
            }
        }
    }
```

- [ ] **Step 4: Run the servers tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*OpenApiServersTests*'`
Expected: PASS, both facts.

- [ ] **Step 5: Accept the snapshot move**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-method '*The_document_is_stable*'`
It fails with a `.received.txt`. Confirm the ONLY difference is `servers[0].url`:

```bash
diff test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.{verified,received}.txt
```

Expected: one changed line, `"url": "http://localhost/"` → `"url": "http://localhost"`. Then accept it:

```bash
mv test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.{received,verified}.txt
```

The Stop-hook gate will require the `alvo-snapshot-judge` verdict for this baseline — that is expected, and this task's diff is the justification.

- [ ] **Step 6: Run ring0**

Run: `scripts/test-ring0`
Expected: OK.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt
git commit -m "fix(api): the advertised server URL is appendable, per OAS 3.1.1 (#26)"
```

---

### Task 3: The ruleset and `scripts/lint-api`

**Files:**
- Create: `schema/openapi-ruleset.yaml`
- Create: `scripts/lint-api`

**Interfaces:**
- Consumes: `scripts/ensure-vacuum` (Task 1), the corrected snapshot (Task 2).
- Produces: seven rule ids later tasks mutate — `alvo-operation-id-shape`, `alvo-schema-key-casing`, `alvo-parameter-key-casing`, `alvo-response-key-casing`, `alvo-response-described`, `alvo-response-no-store`, `alvo-problem-media-type`.

- [ ] **Step 1: Write the ruleset**

Create `schema/openapi-ruleset.yaml` exactly as below. Every mute carries its reason; every Alvo rule was measured to fire.

```yaml
# Alvo's OpenAPI contract ruleset — the one authority on the generated document's static shape.
#
# Run by scripts/lint-api (the committed snapshot) and by
# MMLib.Alvo.Api.Invariants.Tests.Integration (the four examples and 16 generated descriptors).
# Spec §308/§415 ask for exactly this: a Spectral-compatible ruleset in the repo, a Go binary, no Node.
#
# It lives in schema/ beside project.schema.json because both are contracts the repo publishes about a
# generated artifact — neither is test-only.
#
# THREE THINGS MEASURED ABOUT VACUUM, each of which shaped what is written here (v0.30.3):
#   * rules see the RESOLVED document, so a rule asserting a `$ref` is present fires on every operation.
#     The `$ref` spelling is pinned by the document snapshot instead; here only resolved shape is asserted.
#   * `field: "@key"` works with `casing` and is SILENTLY VACUOUS with `pattern` — an impossible
#     `match: "^ZZZ"` over $.paths reported zero violations. Hence casing rules, never @key patterns.
#   * JSONPath filter expressions (`[?(...)]`) do not parse, and one bad `given` aborts the whole run.
#     Anything conditional is therefore a C# fact in test/_shared/api/OpenApiDocumentFacts.cs.
extends: [[vacuum:oas, recommended]]
documentationUrl: https://github.com/Burgyn/MMLib.Alvo/blob/main/docs/superpowers/specs/2026-09-08-f4-api-invariants-vacuum-design.md

rules:
  # --- muted built-ins, each with its reason -------------------------------------------------------

  # A schema property name is the descriptor's FIELD name, snake_case by schema
  # (^[a-z][a-z0-9_]{0,62}$). Renaming it in the document would misdescribe the wire. Casing is
  # enforced below on the keys Alvo itself mints. Deviation from spec §308's "camelCase" shorthand.
  camel-case-properties: off

  # A path segment is the entity identifier verbatim — PostgREST's spelling, which Alvo adopts on
  # purpose. Measured: `work_orders` and `invoice_items` in examples/field-service and
  # examples/complex-crm each raise four violations. Rewriting them would be a wire-breaking change to
  # the one convention the framework copied deliberately.
  paths-kebab-case: off

  # Alvo emits no examples. Per-field-type examples are a real agent-first improvement and a PRODUCT
  # change: tracked in #207 rather than bent around here.
  oas3-missing-example: off

  # One generator serves N entities, so identical prose across operations is a property of a
  # metadata-driven document, not a defect.
  description-duplication: off

  # A `json`-typed field is deliberately type-free: in draft 2020-12 an absent `type` means "any",
  # which is exactly the intent.
  oas-missing-type: off

  # DELETE /{entity}/batch carries {"ids":[…]}. RFC 9110 §9.3.5 gives content on a DELETE no defined
  # semantics, and the mirror-image argument is why POST /{entity}/query exists — so this is a real
  # question about the verb, tracked in #206. It is NOT loosened silently: OpenApiDocumentFacts pins
  # exactly which routes carry a DELETE body, so the exemption cannot spread to the single-row delete.
  no-request-body: off

  # --- Alvo's own rules ----------------------------------------------------------------------------

  alvo-operation-id-shape:
    description: "operationId is <entity>.<operation>"
    given: $.paths[*][*].operationId
    severity: error
    then:
      function: pattern
      functionOptions:
        match: "^[a-z][a-zA-Z0-9]*\\.[a-z][a-zA-Z0-9]*$"

  alvo-schema-key-casing:
    description: "framework-minted component schema keys are camelCase"
    given: $.components.schemas
    severity: error
    then:
      field: "@key"
      function: casing
      functionOptions:
        type: camel

  alvo-parameter-key-casing:
    description: "framework-minted component parameter keys are camelCase"
    given: $.components.parameters
    severity: error
    then:
      field: "@key"
      function: casing
      functionOptions:
        type: camel

  alvo-response-key-casing:
    description: "framework-minted component response keys are camelCase"
    given: $.components.responses
    severity: error
    then:
      field: "@key"
      function: casing
      functionOptions:
        type: camel

  alvo-response-described:
    description: "every response carries a description"
    given: $.paths[*][*].responses[*]
    severity: error
    then:
      field: description
      function: truthy

  alvo-response-no-store:
    description: "every response documents Cache-Control"
    given: $.paths[*][*].responses[*].headers
    severity: error
    then:
      field: Cache-Control
      function: truthy

  alvo-problem-media-type:
    description: "every declared error response is a problem+json document"
    given: $.components.responses[*].content
    severity: error
    then:
      field: application/problem+json
      function: truthy
```

- [ ] **Step 2: Write `scripts/lint-api`**

```bash
#!/usr/bin/env bash
# lint-api — lint the committed OpenAPI document snapshot against schema/openapi-ruleset.yaml.
#
# The cheap half of #26's static gate: no build, no .NET, no running host, so it fires the moment
# someone edits the snapshot. The other caller of the same ruleset is
# MMLib.Alvo.Api.Invariants.Tests.Integration, which lints the four examples and 16 generated
# documents over a running host. One ruleset file, two callers — never two authorities.
#
# --fail-severity warn, not the default `error`: after the mutes the ruleset declares, the document is
# clean at EVERY severity, so a new warning is a regression and not noise to be accumulated.
#
# vacuum sniffs content rather than the file extension (measured), so the `.verified.txt` snapshot is
# linted in place rather than copied to a `.json` first.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DIR/.." && pwd)"
VACUUM="$("$DIR/ensure-vacuum")"
RULESET="$ROOT/schema/openapi-ruleset.yaml"
DOCUMENT="$ROOT/test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt"

echo "[lint-api] vacuum lint $(basename "$DOCUMENT")"
"$VACUUM" lint --no-style --no-banner --details --all-results \
  --fail-severity warn --ruleset "$RULESET" "$DOCUMENT"
echo "[lint-api] OK"
```

- [ ] **Step 3: Run it to verify it passes on the corrected snapshot**

Run: `chmod +x scripts/lint-api && scripts/lint-api`
Expected: `Quality Score: 100/100`, then `[lint-api] OK`. If `oas3-api-servers` appears, Task 2 was not applied.

- [ ] **Step 4: Verify the gate is not vacuous at the script level**

```bash
python3 - <<'PY'
import json,shutil
p='test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt'
shutil.copy(p,'/tmp/snapshot.bak')
d=json.load(open(p)); d['paths']['/api/categories']['get']['operationId']='CategoriesList'
json.dump(d,open(p,'w'))
PY
scripts/lint-api; echo "EXIT=$?"          # must be non-zero, naming the operationId rule
cp /tmp/snapshot.bak test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt
scripts/lint-api                           # green again
```

Expected: the middle run exits non-zero and its details name `operationId is <entity>.<operation>`.

- [ ] **Step 5: Commit**

```bash
git add schema/openapi-ruleset.yaml scripts/lint-api
git commit -m "feat(schema): Alvo's OpenAPI contract ruleset + the lint script (#26)"
```

---

### Task 4: Generic document facts, shared by every caller

**Files:**
- Create: `test/_shared/api/OpenApiDocumentFacts.cs`
- Modify: `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs` (one new fact)

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static class OpenApiDocumentFacts` with
  `static void AssertShape(JsonObject document, IReadOnlyCollection<string> entities)`, which throws a
  Shouldly assertion on the first violated claim. `IReadOnlyCollection` so both a `string[]` (the
  fixture's `_entities`) and the generator's `IReadOnlyList<string>` bind without a copy. Task 6 calls
  exactly this.

- [ ] **Step 1: Write the failing fact in the existing suite**

Add to `OpenApiDocumentTests`:

```csharp
    /// <summary>
    /// The document satisfies the <em>generic</em> shape claims — the ones every Alvo document must
    /// satisfy, whatever the descriptor said.
    /// </summary>
    /// <remarks>
    /// The facts themselves live in <see cref="OpenApiDocumentFacts"/> because
    /// <c>MMLib.Alvo.Api.Invariants.Tests.Integration</c> applies the same ones to the four
    /// <c>examples/</c> descriptors and to sixteen generated ones (#26). The claims here are the
    /// conditional and cross-referential ones the Vacuum ruleset cannot express — measured: JSONPath
    /// filters do not parse, and the `schema` function's `if`/`then` reports `` `` for every operation.
    /// The rest of this file stays what it is: the fixture's own detailed pins.
    /// </remarks>
    [Fact]
    public async Task The_document_satisfies_the_generic_shape_facts()
    {
        await using var world = await StoreAsync();

        OpenApiDocumentFacts.AssertShape(await world.OpenApiDocumentAsync(), _entities);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-method '*generic_shape_facts*'`
Expected: FAIL to compile — `OpenApiDocumentFacts` does not exist.

- [ ] **Step 3: Write `test/_shared/api/OpenApiDocumentFacts.cs`**

An `internal static class` in namespace `MMLib.Alvo.Api.Tests` (the namespace every `_shared/api` file uses), with `AssertShape` calling one private method per claim. Keep each method under ~25 lines — the repo's extraction rule.

The claims, in order, each with the reason in a `<remarks>`:

1. `ThePathSetIsExactlyTheEntitiesRoutes` — the path key set equals
   `entities × { "", "/query", "/{id}", "/batch" }` with the route prefix `/api`, and the count is
   asserted against `entities.Count * 4` first so an equality of two empty sets cannot pass.
2. `NoPathSegmentIsNumeric` — every segment of every path key either is `{id}` or contains a
   non-digit. Measured: no built-in vacuum rule covers this, and the `@key`+`pattern` form is vacuous,
   which is why the "no integers in URLs" rule of spec §308 is here.
3. `EveryListOperationDocumentsThePagingParameters` — for each `/api/<entity>` `get`, the parameter
   names (following `$ref`s into `components.parameters`) are a superset of
   `["prefer","select","order","limit","offset","after"]`.
4. `EveryListResponseIsAPageEnvelope` — that operation's `200` schema resolves to an object whose
   `properties` contain `items`, `next` and `count`, all three.
5. `EveryRefusalIsAProblemDocument` — every response whose status is `>= 400` resolves to a
   `application/problem+json` content whose schema resolves to `problemDetails`.
6. `OnlyTheBatchRouteCarriesADeleteBody` — a `delete` operation has a `requestBody` if and only if its
   path ends in `/batch`. This is the pin that keeps #206's muted `no-request-body` from spreading.

Helper: `private static JsonObject Resolve(JsonObject document, JsonObject node)` follows a
`$ref` of the form `#/components/<kind>/<name>` one level and returns the node unchanged otherwise.

- [ ] **Step 4: Run the fact to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-method '*generic_shape_facts*'`
Expected: PASS.

- [ ] **Step 5: Prove each claim is non-vacuous**

For each of the six claims, temporarily break the document in a scratch copy of the fact (mutate the
`JsonObject` before calling `AssertShape`) and confirm the specific claim fails. Do this in the REPL of
your editor or as a throwaway `[Fact]` you delete before committing — the permanent negative proofs for
the *ruleset* are Task 5's; these six are C# claims whose mutation coverage is proven once, here, and
recorded in the commit message.

Expected: six distinct failures, each naming its own claim.

- [ ] **Step 6: Commit**

```bash
git add test/_shared/api/OpenApiDocumentFacts.cs test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs
git commit -m "test(api): generic document shape facts, shared by every caller (#26)"
```

---

### Task 5: The invariant project, the vacuum runner, and the lint battery

**Files:**
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/MMLib.Alvo.Api.Invariants.Tests.Integration.csproj`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/AssemblyInfo.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/VacuumRunner.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/RulesetMutation.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/RulesetTests.cs`
- Modify: `MMLib.Alvo.slnx`
- Modify: `src/MMLib.Alvo/Properties/AssemblyInfo.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs`

**Interfaces:**
- Consumes: `schema/openapi-ruleset.yaml`, `scripts/ensure-vacuum` (both Task 1/3).
- Produces:
  - `internal sealed record VacuumViolation(string Code, int Severity, string Message)`
  - `internal static class VacuumRunner` with
    `static IReadOnlyList<VacuumViolation> Lint(JsonObject document, string name)` — writes the
    document to a temp file, runs `spectral-report`, returns every violation of severity ≤ 1; throws
    `InvalidOperationException` if vacuum exits with anything but 0 or 1.
  - `internal static class RulesetMutation` with
    `static IReadOnlyList<(string RuleId, Action<JsonObject> Apply)> All` and
    `static IReadOnlySet<string> AlvoRuleIds(string rulesetPath)`.

- [ ] **Step 1: Create the project**

`MMLib.Alvo.Api.Invariants.Tests.Integration.csproj` — copy `MMLib.Alvo.Api.Tests.Integration.csproj`'s
shape and drop Testcontainers. It needs: `AlvoSharedArchTests=false`,
`InterceptorsNamespaces` += `Microsoft.AspNetCore.OpenApi.Generated`,
`FrameworkReference Microsoft.AspNetCore.App`, project references to `MMLib.Alvo` and
`MMLib.Alvo.Data.Sqlite`, package references `Microsoft.AspNetCore.TestHost` and `CsCheck`, and the
same two linked-source item groups (`../_shared/ef/SqlCapture.cs`, `../_shared/api/*.cs`). Its header
comment must say why it exists as a separate project and why it is `.Tests.Integration`:

```
    #26's API invariant suite: the claim that Alvo's rules hold ACROSS descriptors rather than for the
    one demo they were written against. It boots 16 generated descriptors plus the four in examples/,
    so ".Tests.Integration" — scripts/test-ring0 runs `*.Tests.dll` and excludes this by naming, which
    is what keeps ring0 fast. ring2 runs it, affected-scoped, and CI runs ring2 on both OSes.

    Not folded into MMLib.Alvo.Api.Tests.Integration: that project is the PostgreSQL leg and skips
    itself where Docker cannot run a Linux image, which is exactly the invisibility this suite must
    not inherit (CI sets TESTINGPLATFORM_EXITCODE_IGNORE=8 on Windows). This one needs no container.
```

Add it to `MMLib.Alvo.slnx` in the `/test/` folder, alphabetically. Add
`[assembly: InternalsVisibleTo("MMLib.Alvo.Api.Invariants.Tests.Integration")]` to both
`src/MMLib.Alvo/Properties/AssemblyInfo.cs` and `src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs`
(the linked `_shared/api` sources use internals of both), each with a one-line reason in the repo's style.

- [ ] **Step 2: Write the failing ruleset test**

`RulesetTests.cs`. Every test class in this project declares the same two fixtures at the top — the
repo's idiom is a private static per class rather than a shared base:

```csharp
    /// <summary>A key that authenticates and carries every scope, so a 403 can only be a missing rule.</summary>
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>A world that serves its OpenAPI document, which is what the static half lints.</summary>
    private static readonly AlvoApiWorldSetup _documented = new(MapOpenApiDocument: true);

    /// <summary>The one ruleset both callers use.</summary>
    private static readonly string Ruleset =
        Path.Combine(RepositoryRoot.Find(), "schema", "openapi-ruleset.yaml");
```

Then the facts:

```csharp
    /// <summary>
    /// Every Alvo rule in the ruleset has a mutation that proves it fires — and the ruleset has no rule
    /// without one.
    /// </summary>
    /// <remarks>
    /// The set is read out of the YAML rather than restated here, so a rule added later cannot arrive
    /// unproven. That is not hypothetical: `field: "@key"` with `pattern` is SILENTLY VACUOUS in
    /// vacuum 0.30.3 — an impossible `match: "^ZZZ"` over $.paths reports zero violations — so a rule
    /// that tests nothing looks exactly like a rule that passes.
    /// </remarks>
    [Fact]
    public void Every_alvo_rule_has_a_mutation_that_proves_it_fires()
    {
        var declared = RulesetMutation.AlvoRuleIds(Ruleset);

        RulesetMutation.All.Select(mutation => mutation.RuleId)
            .Where(id => id.StartsWith("alvo-", StringComparison.Ordinal))
            .ToHashSet()
            .ShouldBe(declared, ignoreOrder: true, "a rule with no mutation is a rule nothing holds");
    }
```

plus the battery itself:

```csharp
    /// <summary>A clean document reports nothing at error or warning severity.</summary>
    [Fact]
    public async Task The_fixture_document_is_clean_under_the_ruleset()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], _documented);

        VacuumRunner.Lint(await world.OpenApiDocumentAsync(), "vehicle-registry").ShouldBeEmpty();
    }

    /// <summary>Each mutation is reported under its own rule id, and a clean document is not.</summary>
    /// <param name="ruleId">The rule the mutation violates.</param>
    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task A_deliberate_violation_is_reported_under_its_own_rule(string ruleId)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], _documented);
        var document = await world.OpenApiDocumentAsync();
        RulesetMutation.All.Single(mutation => mutation.RuleId == ruleId).Apply(document);

        var violations = VacuumRunner.Lint(document, ruleId);

        violations.Select(violation => violation.Code).ShouldContain(ruleId);
    }
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: FAIL to compile — `VacuumRunner` and `RulesetMutation` do not exist.

- [ ] **Step 4: Write `VacuumRunner`**

Behaviour, in this order:

1. Resolve the binary **once per process** (`static readonly Lazy<string>`) by running
   `scripts/ensure-vacuum` from `RepositoryRoot.Find()` and taking the last non-empty stdout line. If
   it exits non-zero, throw with its stderr and the instruction to run `scripts/ensure-vacuum`. Never
   skip: a suite that self-skips when the tool is absent is a suite that passes for the wrong reason.
2. Write the document to `Path.Combine(Path.GetTempPath(), "alvo-openapi", $"{name}-{Guid}.json")`.
3. Run `spectral-report --ruleset <ruleset> <document> <report>`, capturing stdout/stderr.
4. Exit code 0 or 1 → parse the report as `JsonArray`; anything else → throw
   `InvalidOperationException` naming the exit code and stderr, because vacuum exits 2 on a crash
   (measured: an unresolvable `$ref` panics it) and a crash must never read as a legitimate red.
5. Return every entry with `severity <= 1`, mapped to `VacuumViolation`.
6. Delete both temp files in a `finally`.

- [ ] **Step 5: Write `RulesetMutation`**

`AlvoRuleIds(path)` reads the YAML **without a YAML parser** — the ruleset's rule ids are the only
lines matching `^  (alvo-[a-z-]+):$`, and adding a dependency to read six lines is not worth it. Say so
in a comment.

`All` is the eight-entry table below. Every mutation is **structural**: a textual rename breaks every
`$ref` to the key and vacuum answers that with `unable to build unresolved model` — or, in one shape, a
panic — so the mutation would measure the resolver rather than the rule. Both were hit while designing
this. `Rekey` therefore rewrites every `$ref` to the renamed component.

| Rule id | Mutation |
|---|---|
| `alvo-operation-id-shape` | `/api/owners` `get`'s `operationId` becomes `OwnersList` |
| `alvo-schema-key-casing` | `Rekey("schemas", "ownersPage", "owners_page")` |
| `alvo-parameter-key-casing` | `Rekey("parameters", "ifMatch", "if_match")` |
| `alvo-response-key-casing` | `Rekey("responses", "forbidden", "not_allowed")` |
| `alvo-response-described` | `/api/owners` `get`'s `200.description` is removed |
| `alvo-response-no-store` | `/api/owners` `get`'s `200.headers.Cache-Control` is removed |
| `alvo-problem-media-type` | `components.responses.forbidden.content`'s `application/problem+json` key becomes `application/json` |
| `operation-operationId-unique` | `/api/vehicles` `get`'s `operationId` is set to `/api/owners` `get`'s — the one non-`alvo-` entry, which proves `extends: recommended` is still live |

- [ ] **Step 6: Run the suite to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS — 1 completeness fact, 1 clean-document fact, 8 mutation cases.

- [ ] **Step 7: Commit**

```bash
git add test/MMLib.Alvo.Api.Invariants.Tests.Integration MMLib.Alvo.slnx src/MMLib.Alvo/Properties/AssemblyInfo.cs src/MMLib.Alvo.Abstractions/Properties/AssemblyInfo.cs
git commit -m "test(api): the invariant project, the vacuum runner, and the lint battery (#26)"
```

---

### Task 6: The descriptor generator, and the document half of the invariants

**Files:**
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/GeneratedProject.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/GeneratedProjectTests.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/ExampleProjectTests.cs`
- Modify: `test/_shared/api/AlvoApiWorld.cs`

**Interfaces:**
- Consumes: `OpenApiDocumentFacts.AssertShape` (Task 4), `VacuumRunner.Lint` (Task 5).
- Produces:
  - `internal sealed record GeneratedProject(string Name, string Json, IReadOnlyList<string> Entities, IReadOnlyList<string> PermissiveEntities, IReadOnlyList<string> DeniedEntities, bool TenancyEnabled)`
  - `internal static GeneratedProject Generate(int seed)`
  - `internal static IReadOnlyList<int> Seeds` — the 16 committed seeds, overridable by
    `ALVO_INVARIANT_SEED` / `ALVO_INVARIANT_N`. **Every test class that drives them declares its own
    `public static TheoryData<int> Seeds => [.. GeneratedProject.Seeds];`**, because `[MemberData]`
    resolves the member on the test class and needs `TheoryData`/`IEnumerable<object[]>`, not a bare
    `IReadOnlyList<int>`.
  - `internal Task<AlvoApiWorld> WriteAndStartAsync(IReadOnlyList<TestApiKey> keys, AlvoApiWorldSetup? setup = null)`
  - `AlvoApiWorld.FromDescriptorPathAsync(string descriptorPath, IReadOnlyList<TestApiKey>? keys = null, AlvoApiWorldSetup? setup = null)`

- [ ] **Step 1: Add the absolute-path entry point to the shared world**

In `test/_shared/api/AlvoApiWorld.cs`, beside `FromDescriptorAsync`:

```csharp
    /// <summary>Starts a world over a descriptor at an absolute path.</summary>
    /// <param name="descriptorPath">The descriptor file's full path.</param>
    /// <param name="keys">The dev API keys the world issues.</param>
    /// <param name="setup">Anything the world's host is configured differently from the default.</param>
    /// <remarks>
    /// <see cref="FromDescriptorAsync"/> resolves a name under the test project's own
    /// <c>descriptors/</c> directory, which a <em>generated</em> descriptor has no place in: #26's
    /// invariant suite writes each of its sixteen to a temp directory and would otherwise have to
    /// pollute a shipped fixture folder to boot one.
    /// </remarks>
    internal static Task<AlvoApiWorld> FromDescriptorPathAsync(
        string descriptorPath, IReadOnlyList<TestApiKey>? keys = null, AlvoApiWorldSetup? setup = null) =>
        StartAsync(
            descriptorPath, keys ?? [], setup ?? new AlvoApiWorldSetup(), SqliteApiEngine.Instance);
```

- [ ] **Step 2: Write the failing generator tests**

`GeneratedProjectTests.cs`, three facts before any invariant runs — the generator is a fixture and a
broken fixture must fail as itself:

```csharp
    /// <summary>Every generated descriptor validates against the frozen project schema.</summary>
    /// <remarks>
    /// Asserted before any invariant runs, so a generator bug fails as a generator bug rather than as
    /// sixteen mysterious API failures. The schema is the one in <c>schema/</c> — the same file
    /// <c>MMLib.Alvo.Schema.Tests</c> validates the shipped examples against.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_generated_descriptor_is_valid(int seed) { /* SchemaValidator.Failures(...) is empty */ }

    /// <summary>The same seed generates the same descriptor, byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Generation_is_deterministic(int seed) { /* Generate(seed).Json == Generate(seed).Json */ }

    /// <summary>
    /// The sixteen seeds together cover every field type, both tenancy settings, and entities with and
    /// without rules.
    /// </summary>
    /// <remarks>
    /// The coverage set is pinned from OUTSIDE the generator — the eleven names come from the frozen
    /// schema's own <c>fieldType</c> enum, read off the file — so "covers every field type" cannot be
    /// satisfied by a generator that produces one type and a test that asks about one type.
    /// </remarks>
    [Fact]
    public void The_seed_set_covers_every_field_type_and_both_tenancy_settings() { }
```

`SchemaValidator` lives in `MMLib.Alvo.Schema.Tests`. Do **not** reference that project: move
`SchemaValidator.cs` and `SchemaPaths.cs` to `test/_shared/schema/` and link them into both projects
(the same arrangement `_shared/api` already has), keeping the namespace they have so
`MMLib.Alvo.Schema.Tests` needs no edit beyond its csproj link.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration --filter-class '*GeneratedProjectTests*'`
Expected: FAIL to compile — `GeneratedProject` does not exist.

- [ ] **Step 4: Write the generator**

`GeneratedProject.Generate(seed)` composes the descriptor with CsCheck `Gen`, seeded deterministically
(`Gen.…Single(seed)`-style: draw from a `PCG` created from the seed so the same seed yields the same
descriptor). Constraints, all of them from the frozen schema and the core's own validator:

- 1–3 entities, names drawn from a fixed non-verb, non-reserved word list (`owners`, `vehicles`,
  `work_orders`, `invoice_items`, …) — snake_case multi-word names are *deliberately* in the pool,
  since they are what `paths-kebab-case` would have flagged.
- 1–6 fields per entity, names from a word pool, never a `ReservedQueryKeys` name and never a managed
  column (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at`) — ask
  `ReservedQueryKeys.IsReserved` rather than copying the list.
- field types drawn from the schema's eleven, with the schema's own conditional requirements honoured:
  `enum` carries `values` (≥1, unique), `ref` carries `entity` naming an entity generated *before* it,
  `decimal` carries `precision` (1–38) and `scale` (0–precision), `maxLength` only on `string`.
- `required`/`unique`/`nullable` toggled; no `computed`, no `rollup`, no `hidden`, no `readOnly` —
  those are the descriptor's *unhonoured* or masking features and belong to their own suites.
- `softDelete` and `audit` toggled per entity; `tenancy.enabled` toggled per project, and when it is on
  each entity is `scoped` or `global`.
- **At least one entity with `rules` and at least one without**, which is what makes the default-deny
  and the CRUD invariants both reachable in every case. A permissive entity's rules are exactly
  `"true"` for all five operations — deliberately not a role expression, because Task 8's sabotage
  substitutes one entity's decision for another's and a predicate naming a column would then fail for
  the wrong reason.

`Seeds` is `[1..16]` unless `ALVO_INVARIANT_N` overrides the count or `ALVO_INVARIANT_SEED` names a
first seed. Say in a comment why the seeds are committed rather than random: a random-seeded property
suite on a required PR check is a flake generator, and this suite boots a host per case so shrinking
costs more than the descriptor dump it would replace.

`WriteAndStartAsync` writes `Json` to
`Path.Combine(Path.GetTempPath(), "alvo-invariants", $"{Name}.alvo.json")` and calls
`AlvoApiWorld.FromDescriptorPathAsync`.

- [ ] **Step 5: Run the generator facts to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration --filter-class '*GeneratedProjectTests*'`
Expected: PASS — 16 + 16 + 1.

- [ ] **Step 6: Add the document invariants over the generated projects and the examples**

In `GeneratedProjectTests` (which declares `_admin`, `_documented` and
`public static TheoryData<int> Seeds => [.. GeneratedProject.Seeds];` exactly as `RulesetTests` does):

```csharp
    /// <summary>Every generated project's document is clean under the ruleset and generically well shaped.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task A_generated_projects_document_holds_the_contract(int seed)
    {
        var project = GeneratedProject.Generate(seed);
        await using var world = await project.WriteAndStartAsync([_admin], _documented);
        var document = await world.OpenApiDocumentAsync();

        VacuumRunner.Lint(document, project.Name).ShouldBeEmpty();
        OpenApiDocumentFacts.AssertShape(document, project.Entities);
    }
```

And `ExampleProjectTests` does the same over the four `examples/*/*.alvo.json` paths, discovered with
`SchemaPaths.Examples()` so the set comes from the repository rather than from a list here — spec §415's
"against the demo's OpenAPI", for every descriptor the compose stacks actually serve.

- [ ] **Step 7: Run and verify**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS. Record the wall-clock time — the design's budget is under ~60s for this project.

- [ ] **Step 8: Commit**

```bash
git add test/MMLib.Alvo.Api.Invariants.Tests.Integration test/_shared/api/AlvoApiWorld.cs test/_shared/schema test/MMLib.Alvo.Schema.Tests
git commit -m "test(api): generated descriptors, and the document contract across them (#26)"
```

---

### Task 7: The behavioural invariants across descriptors

**Files:**
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/BehaviourInvariants.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/BehaviourInvariantTests.cs`

**Interfaces:**
- Consumes: `GeneratedProject` (Task 6).
- Produces: `internal static class BehaviourInvariants` with four methods a sabotaged run re-uses
  verbatim — `Task DefaultDenyAsync(AlvoApiWorld, GeneratedProject)`,
  `Task CrudShapeAsync(AlvoApiWorld, GeneratedProject)`,
  `Task ReplaceIsIdempotentAsync(AlvoApiWorld, GeneratedProject)`,
  `Task IdempotencyKeyWritesOneRowAsync(AlvoApiWorld, GeneratedProject)`.

- [ ] **Step 1: Write the failing test**

```csharp
    /// <summary>
    /// Every generated project holds the four behavioural invariants — the claim #26 actually makes:
    /// they hold across descriptors, not for the one demo they were written against.
    /// </summary>
    /// <param name="seed">The generated project's seed, which is also how a failure is reproduced.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task A_generated_project_holds_every_behavioural_invariant(int seed)
    {
        var project = GeneratedProject.Generate(seed);
        await using var world = await project.WriteAndStartAsync([_admin]);

        await BehaviourInvariants.DefaultDenyAsync(world, project);
        await BehaviourInvariants.CrudShapeAsync(world, project);
        await BehaviourInvariants.ReplaceIsIdempotentAsync(world, project);
        await BehaviourInvariants.IdempotencyKeyWritesOneRowAsync(world, project);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration --filter-class '*BehaviourInvariantTests*'`
Expected: FAIL to compile — `BehaviourInvariants` does not exist.

- [ ] **Step 3: Write the four invariants**

`DefaultDenyAsync` — for each entity in `project.DeniedEntities`, every one of the ten routes answers
403 to `_admin` (a key with `*:read`/`*:write`, so a 403 can only be the missing rule), and
`world.Statements` is empty after `ClearStatements()`: default-deny must refuse before the port
composes a statement. Assert the route count reached is `10` per entity so a typo in a path cannot make
this pass by testing nothing.

`CrudShapeAsync` — for the first entity in `project.PermissiveEntities`: `POST` a body built from the
entity's own generated fields → 201 with a `Location`; `GET` that location → 200; `GET` the collection →
an envelope with `items`, `next`, `count`; `PATCH` one field → 200; `DELETE` → 204; `GET` again → 404,
and its body is a problem document whose `type` starts `https://alvo.dev/errors/`.

`ReplaceIsIdempotentAsync` — `PUT` the same body twice and compare the whole row, **from two different
starting states**: once over a row created with every optional field set, once over a row created with
none. That is the property a merge cannot satisfy, and it is the one #105's port suite already holds for
a single descriptor.

`IdempotencyKeyWritesOneRowAsync` — `POST` twice with the same `Idempotency-Key`, then
`world.CountRowsAsync(entity)` is `1`. Counted from the table, never from a list: a list is filtered by
the caller's policy and would hide a second row rather than report it.

A body builder — `internal static JsonObject Body(GeneratedProject project, string entity, bool optionalFields)` —
produces a valid value per generated field type (`string` → a short literal honouring `maxLength`,
`integer` → 1, `decimal` → a value inside `precision`/`scale`, `boolean` → true, `date` → `2026-01-01`,
`datetime` → an ISO-8601 instant, `uuid` → a fixed Guid, `json` → `{}`, `enum` → its first declared
value, `ref` → the id of a row seeded in the target entity first, `text` → a short literal).

- [ ] **Step 4: Run and verify**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS, all 16 behaviour cases.

- [ ] **Step 5: Commit**

```bash
git add test/MMLib.Alvo.Api.Invariants.Tests.Integration
git commit -m "test(api): default-deny, CRUD shape and idempotency across generated descriptors (#26)"
```

---

### Task 8: The sabotage battery — proving the invariants can fail

**Files:**
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/Sabotage.cs`
- Create: `test/MMLib.Alvo.Api.Invariants.Tests.Integration/SabotageTests.cs`

**Interfaces:**
- Consumes: `BehaviourInvariants` (Task 7), `ServiceDecoration` and
  `AlvoApiWorldSetup.ConfigureServicesAfterAlvo` (both already in `_shared/api`).
- Produces: `internal static class Sabotage` with
  `static AlvoApiWorldSetup PolicyAdmitsEverything(GeneratedProject project)` and
  `static AlvoApiWorldSetup IdempotencyForgets()`.

- [ ] **Step 1: Write the failing test**

```csharp
    /// <summary>
    /// Default-deny's invariant goes RED when the policy engine stops denying.
    /// </summary>
    /// <remarks>
    /// #26's DoD names this explicitly, and it is the half that keeps the suite from becoming a green
    /// check nobody trusts: an invariant that cannot fail proves nothing about the code it watches. The
    /// assertion is only that it fails — not how. A sabotaged engine may answer 200 where a denial was
    /// due, or throw while applying one entity's predicate to another's rows; either is the suite
    /// noticing, and pinning one of them would pin the sabotage rather than the invariant.
    /// </remarks>
    [Fact]
    public async Task Breaking_default_deny_makes_its_invariant_fail()
    {
        var project = GeneratedProject.Generate(GeneratedProject.Seeds.First());
        await using var world = await project.WriteAndStartAsync([_admin], Sabotage.PolicyAdmitsEverything(project));

        await Should.ThrowAsync<Exception>(() => BehaviourInvariants.DefaultDenyAsync(world, project));
    }

    /// <summary>The idempotency invariant goes RED when the token stops being honoured.</summary>
    [Fact]
    public async Task Breaking_idempotency_makes_its_invariant_fail()
    {
        var project = GeneratedProject.Generate(GeneratedProject.Seeds.First());
        await using var world = await project.WriteAndStartAsync([_admin], Sabotage.IdempotencyForgets());

        await Should.ThrowAsync<Exception>(
            () => BehaviourInvariants.IdempotencyKeyWritesOneRowAsync(world, project));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration --filter-class '*SabotageTests*'`
Expected: FAIL to compile — `Sabotage` does not exist.

- [ ] **Step 3: Write the two saboteurs**

`PolicyAdmitsEverything` decorates `IPolicyEngine` through `ServiceDecoration`: when `Resolve` returns
a decision with `IsDenied`, it re-resolves for `project.PermissiveEntities[0]` and returns *that*
decision. Note in a comment why it is written this way rather than constructing an allow:
`PolicyDecision.Allow` is deliberately `internal` to Abstractions, and forging one from a test would be
a test that reaches past the port's own guarantee. Substituting a real allow needs nothing internal —
which is also why a permissive entity's rules are the literal `"true"` (Task 6): the substituted
predicate must not name a column the other entity lacks.

`IdempotencyForgets` decorates `IAlvoData` and forwards every member verbatim **except** the ones
taking an `AlvoIdempotency?`, which it forwards with `null`. So the token is accepted at the HTTP tier
and never reaches the store, which is exactly the regression the invariant claims to catch.

- [ ] **Step 4: Run and verify**

Run: `dotnet test --project test/MMLib.Alvo.Api.Invariants.Tests.Integration`
Expected: PASS — the sabotaged invariants throw, as asserted.

- [ ] **Step 5: Commit**

```bash
git add test/MMLib.Alvo.Api.Invariants.Tests.Integration
git commit -m "test(api): sabotage battery — the invariants can actually fail (#26)"
```

---

### Task 9: Retire the placeholder

**Files:**
- Modify: `scripts/test-ring2`
- Modify: `scripts/test-ring2.ps1`
- Modify: `scripts/test-ring0` (its header comment names the placeholder)
- Modify: `.github/workflows/ci.yml` (the `Test (rings)` comment)
- Modify: `docs/PLAN.md` (§3a)
- Modify: `CLAUDE.md` (the ring table's "placeholder today" wording, if it still applies)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing new — this is the wiring that makes the gate real.

- [ ] **Step 1: Replace the placeholder in `scripts/test-ring2`**

Delete `echo "[ring2] placeholder: API invariant + Vacuum — land in a later F1 PR"`. Before the
integration loop, add:

```bash
# The invariant project shells out to vacuum per generated document, so the binary is resolved BEFORE
# the loop rather than lazily inside sixteen parallel test cases.
echo "[ring2] resolving the pinned Vacuum binary"
"$DIR/ensure-vacuum" >/dev/null
```

and after the loop:

```bash
"$DIR/lint-api"
```

Update the script's header comment: ring2 is now "ring1 + integration (affected) + API invariant +
Vacuum", with none of it a placeholder. Mirror the change in `scripts/test-ring2.ps1`.

- [ ] **Step 2: Correct the comments that promise a placeholder**

`scripts/test-ring0`'s header, and `ci.yml`'s `Test (rings)` comment (which says "plus the
API-invariant/Vacuum placeholders") — both now name the real project.

- [ ] **Step 3: Update `docs/PLAN.md` §3a**

`#26` moves out of "What remains" into the done table (`| API contract lint + invariants | the ruleset
in `schema/`, 16 generated descriptors + the four examples, both halves proven non-vacuous |`), and the
remaining list becomes #24 (with #191) alone. Note #206 and #207 as filed follow-ups. Leave
`← YOU ARE HERE` where it is: F4 still has #24.

- [ ] **Step 4: Run ring2 end to end**

Run: `scripts/test-ring2`
Expected: OK, and the output shows the vacuum resolution, the invariant project running, and
`[lint-api] OK`. Record the total wall-clock time.

- [ ] **Step 5: Commit**

```bash
git add scripts/test-ring2 scripts/test-ring2.ps1 scripts/test-ring0 .github/workflows/ci.yml docs/PLAN.md CLAUDE.md
git commit -m "build: ring2 runs the API invariant suite and the Vacuum lint (#26)"
```

---

## Definition of Done

- [ ] `scripts/lint-api` is green, and non-zero on a deliberate violation (Task 3, step 4).
- [ ] `Every_alvo_rule_has_a_mutation_that_proves_it_fires` passes, and all 8 mutation cases are attributed to their own rule id (Task 5).
- [ ] 16 generated descriptors + 4 examples hold the document contract (Task 6) and the behavioural invariants (Task 7).
- [ ] Sabotaging default-deny and idempotency turns their invariants red (Task 8).
- [ ] `scripts/test-ring2` prints no placeholder, and its measured wall-clock time is reported in the PR.
- [ ] `alvo-plan-guard` dispatched; then the `alvo-pr-report` skill; then the PR.

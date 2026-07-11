# Alvo — implementačná špecifikácia

> **Alvo** · *Application Layer for Vision & Operations* · „Your intent, running in production."
> .NET-native Backend-as-a-Service pre agentický vek. NuGet rodina `MMLib.Alvo.*`.
>
> Tento dokument je **stratégia dodávky a technická špecifikácia** — dopĺňa doménovú analýzu (`baas-analyza.html`), ktorá definuje cieľový produktový záber. Analýza hovorí *čo a prečo*; táto špecifikácia hovorí *ako a v akom poradí*.

---

## 0. Princípy, ktoré riadia celú implementáciu

- [x] *prečítané*

1. **Interface-first.** Najprv rozhrania a kontrakty, potom testy proti nim, až potom implementácia. Rozhrania sú verejné API frameworku — menia sa najdrahšie, preto sa navrhujú prvé.
2. **Provider model všade.** Každá infraštruktúrna schopnosť je port s vymeniteľnými adaptérmi (Azure / Kubernetes / on-prem / in-memory pre testy). Jadro nikdy nesiaha na konkrétnu infraštruktúru priamo.
3. **Engine-agnostické jadro.** Rule engine, event systém a tenancy sú aplikačné — fungujú identicky na SQLite (dev), PostgreSQL aj Azure SQL (produkcia). Natívne mechanizmy DB (RLS, WAL, Change Tracking) sú voliteľný hardening, nie závislosť.
4. **Agent-first.** Agent je primárny používateľ. Konkrétne:
   - Deklaratívna konfigurácia (descriptor / schema-as-code, rules-as-code), štruktúrované chyby (RFC 7807 + návrh opravy), idempotentné operácie.
   - Backend sa stavia **generovaním descriptora proti JSON Schéme** — žiadny protokol navyše. **MCP je voliteľný adaptér nad Management API** pre externých agentov nad bežiacou inštanciou, nie stavebný kameň.
   - **Descriptor je formát, nie miesto — jeden kanonický tvar, dva zdroje pravdy:** súbor v repe (GitOps) alebo záznam v DB bežiacej inštancie (dashboard-first); most je obojsmerný export/import.
   - **Bezpečnosť podľa cesty:** compile-time istota (C# codegen + Roslyn) je bonus GitOps/embedded cesty; dashboard-first nemá build, stojí na runtime validácii descriptora proti JSON Schéme a kontrole pri apply.
5. **Secure-by-default.** Nič nie je exponované bez explicitnej politiky. Default = deny.
6. **Jeden jazyk pre podmienky, jeden pre transformácie** — ostrá hranica úloh:
   - **Podmienky/predikáty** (authorization rules, hook conditions, automation conditions, computed fields): **CEL podmnožina** (Common Expression Language) — non-Turing-complete, safe-by-construction, vlastný compiler s dvomi backendmi: (a) parametrizovaný SQL predikát, (b) kompilované delegáty pre in-memory vyhodnotenie nad event payloadom. Rozšírenia: `changed(field)`, `old`/`new`, `@user`/`@tenant` kontext. Preberáme CEL *spec a syntax* (známa agentom, dokumentovaná), nie hotovú knižnicu — .NET porty sú nezrelé a SQL backend neexistuje nikde.
   - **Transformácie** (payload mapping pre webhooky a akcie, `transform` operácia): **JSONata** (vzor AWS Step Functions) — JSON→JSON transformačná sila; `{{...}}` šablóny ako syntaktický cukor pre jednoduché dosadenia. **JSONata nikdy nebeží in-transaction** (before-hooky a rules = len CEL); evaluátor beží s depth/time limitmi len v after-side akciách.
7. **Descriptor formát: JSON, jediný formát.** Publikovaná JSON Schema (validácia, IntelliSense, spoľahlivá generácia agentmi). Žiadny YAML/JSONC — jeden formát, jeden parser, jedna pravda; export aj API vracia JSON. (Alternatívne ručné formáty možno zvážiť neskôr, ak vznikne dopyt — nie v0.1.)
8. **Minimal API, nie MVC kontrolery.** Všetky endpointy (vygenerované zo schémy aj custom) stoja na minimal API + `RouteGroupBuilder`. Dôvody: konzistencia s .NET 10 smerovaním (`MapIdentityApi` je tiež minimal API), nižšia réžia, a hlavne — endpoint ako delegát sa lepšie **generuje programaticky** zo schémy za behu než kontroler cez reflexiu. Custom endpointy vo vlastnom hoste (§16.2 analýzy) používajú ten istý štýl.
9. **Vertical slice architektúra vnútri balíkov.** Kód sa organizuje po *features* (slice „create record", „dispatch webhook" drží pokope endpoint + handler + validátor + model), nie po technických vrstvách (`Controllers/`, `Services/`, `Validators/`). **Pozor na rozlíšenie úrovní:** rozdelenie na balíky (§1.1) je modulárna architektúra frameworku (verejné API + distribučná/licenčná hranica) — vertical slice je organizácia *vnútri* každého balíka, nie náhrada za balíky. Mediator (tvrdé obmedzenie): **nie MediatR** (od apr 2025 komerčný — §14 analýzy). *Hint:* zváž priame DI handlery alebo Wolverine (MIT, vie aj outbox aj in-process mediator) — rozhodni v brainstormingu.

---

## 0.5 Dva primárne režimy fungovania

- [x] *prečítané*

Alvo má **dva primárne režimy** — a je to jeden kód, dve distribúcie:

### Režim 1 — Standalone (Docker image)

- [x] *prečítané*

```bash
docker run -p 8080:8080 \
  -e ALVO_ADMIN_EMAIL=admin@firma.sk -e ALVO_ADMIN_PASSWORD_FILE=/run/secrets/pwd \
  -e ALVO_ADMIN__PATH=/admin -e ALVO_ADMIN__ALLOWED_IPS=10.0.0.0/8 \
  -e ALVO_SCRIPTS_ALLOW_UI_EDIT=false \
  -v ./projekt.alvo.json:/alvo/descriptor.json \
  mmlib/alvo
```

Stiahneš, spustíš, otvoríš **dashboard** → vytvoríš projekt → máš backend. Alternatívne rovno **podhodíš project descriptor** (JSON) a kontajner naštartuje s hotovým, nakonfigurovaným backendom bez klikania. Pre agentov a automatizáciu existuje **CLI / Management API** — všetko, čo vie dashboard, vie aj API (MCP je voliteľný adaptér nad ním, kontrakt 4).

- **Projekt** = jednotka izolácie v standalone režime: **jedna databáza per projekt** (dev: SQLite súbor per projekt). Nezamieňať s multi-tenancy, ktorá žije *vnútri* projektu.
- **Extensibility v standalone = deklaratívna + csx skripty.** Deklaratívny svet (entity, rules, automation, webhooky) pokrýva bežné prípady; nad ním:
  - **csx skripty (Roslyn):** v mounte/descriptor bundli; compile-on-load s cache podľa content hashu, beh v AssemblyLoadContext, plná Roslyn diagnostika späť agentovi. Skript dostáva `AlvoScriptContext` (dáta cez `IAlvoData` s policy, eventy, logger, limitovaný HTTP klient); before-hooky zo skriptov = rovnaký rozpočet a zákaz siete ako C# hooky.
  - **Trust model natvrdo:** skript = admin-level kód — žiadny sandbox; analyzer proti `System.IO`/raw SQL je defense-in-depth, nie hranica. Editácia z admin UI je opt-in (`ALVO_SCRIPTS_ALLOW_UI_EDIT`, default off), auditovaná, aktivácia po kompilácii + dry-run.
  - **Exekučný model — dve nezávislé osi** (swappable `IFunctionRuntime` port): os 1 = kde beží (in-process / sidecar worker / microVM), os 2 = sync vs queued (outbox + bus). Defaulty: dev = in-process/BackgroundService, self-host = sidecar worker, SaaS = microVM (Container Apps Sandboxes / Kata). Odsun do workera je nezávislý od typu izolácie; untrusted kód (budúci hostovaný SaaS) nikdy in-process.
  - **Hranica režimu:** potreba kompilovaných modulov, vlastných providerov a plného DI = signál na prechod do režimu 2.

### Režim 2 — Embedded (NuGet vo vlastnom hoste)

- [x] *prečítané*

```csharp
builder.Services.AddAlvo(alvo => alvo
    .FromDescriptor("alvo/projekt.alvo.json")   // voliteľne: štart z toho istého descriptora
    .UseDatabase(db => db.UseSqlServer(cs))
    .AddModule<InvoicingModule>()                // vlastné moduly
    .AddAuthorizationHandler<CustomPolicyHandler>() // vlastné autorizácie
    .UseAdmin(a => a.Path("/alvo/admin")            // admin portál: cesta, prístup, brzdy
        .Access(x => x.Admin(u => u.IsInRole("it-admin"))
                      .Viewer(u => u.IsInRole("support")))
        .AllowScriptUiEdit(false))
    .Hooks(h => {
        h.BeforeUpdate<Order>(ctx =>                 // C# hook — plná logika, DI, služby hostu
            ctx.Old.Status == "closed"
                ? HookResult.Reject("Uzavretú objednávku nemožno meniť")
                : HookResult.Continue());
        h.AfterCreate<Order>((ctx, ct) =>
            ctx.Services.GetRequiredService<IErpSync>().PushAsync(ctx.Record, ct)); // after = post-commit, sieť OK
    })
);
```

Pripneš `MMLib.Alvo` do vlastného ASP.NET Core projektu (host). Konfigurácia cez C#, plná extensibility: vlastné moduly, custom autorizácie, hooks, endpointy, providery. ERP scenár.

### Spoločné kontrakty medzi režimami (záväzné)

- [x] *prečítané*

1. **Jeden kód:** standalone image = predpripravený host (`MMLib.Alvo.Host`), ktorý používa ten istý NuGet ako režim 2.
2. **Project descriptor je jednotný artefakt:** mount do Dockera = CLI apply = Management API = `FromDescriptor()` v embedded = export z admin UI. Jeden formát, štyri cesty, identický výsledok. Zároveň migračná cesta standalone → embedded.
3. **Descriptor ≠ infra config:** descriptor = definícia backendu (entity, rules, automation, auth nastavenia, admin access mapping cez CEL + branding); env/secrets = infraštruktúra (bootstrap admin credentials, zapnutie/cesta admin portálu, IP allowlist, `ALVO_SCRIPTS_ALLOW_UI_EDIT`, connection stringy, provideri).
4. **Jedno Management API:** dashboard aj `alvo` CLI sú klienti toho istého API. MCP je voliteľný adaptér nad ním, nie samostatná cesta.
5. **Žiadne default credentials** v image — heslo cez env/secret alebo first-run wizard.

---

## 1. Fáza 1 — Návrh rozhraní + rozdelenie na knižnice

- [x] *prečítané*

Cieľ fázy: **schválené kontrakty a mantinely, nie hotový kód** — porty a ich garancie (§1.2), hranica balíkov (§1.1) a JSON Schema descriptora. Konkrétne signatúry, názvy a presný počet balíkov navrhne agent v brainstorming/plan fáze; táto fáza definuje *čo musí existovať a čo musí garantovať*, nie *ako presne to vyzerá*. **Súčasťou fázy 1 je aj JSON Schema project descriptora** (`alvo-descriptor.schema.json`, publikovaná na `https://alvo.dev/schema/v1/project.json`) — descriptor je centrálny kontrakt všetkých ciest (mount/CLI/API/FromDescriptor/UI export), takže jeho schéma je prvé skutočné API produktu. Vlastnosti: draft 2020-12, `additionalProperties:false` všade (agent zlyhá hneď, nie potichu), popisy pri každom poli (IntelliSense + agenti), podmienené požiadavky per typ poľa (enum→values, ref→entity, decimal→precision/scale), before-hooky štrukturálne obmedzené len na reject/mutate (sieťové akcie sa do nich nedajú ani zapísať). Prvý draft je hotový a zvalidovaný proti CRM príkladu (§16 analýzy) aj negatívnym prípadom. Definition of done: porty a ich garancie sú zdokumentované a odsúhlasené, hranica balíkov je jasná, descriptor schéma validná; **nie hotové signatúry — tie vzniknú pri implementácii.**

### 1.1 Rozdelenie na knižnice — pravidlo, nie hotový zoznam

- [x] *prečítané*

> **Zadanie:** nie „vytvor týchto 10 balíkov", ale „rozdeľ podľa tejto hranice". Presný počet a názvy navrhne agent v brainstormingu; nižšie je hranica (tvrdé pravidlo) + ilustračný príklad (nezáväzný).

**Hranica balíka (tvrdé pravidlo):** samostatný NuGet balík si zaslúži len komponent, ktorý spĺňa aspoň jedno — (a) **ťahá cudziu/ťažkú závislosť**, ktorú väčšina používateľov nechce (Azure SDK, DB driver, Roslyn, Blazor), (b) je **skutočný swap-bod**, ktorý niekto reálne vymení (DB engine, secret store), alebo (c) má **inú distribučnú/licenčnú politiku**. Čokoľvek iné žije ako **namespace / vertical slice vnútri jadra**, nie ako projekt. Konceptuálna úhľadnosť nie je dôvod na balík.

**Dôsledok pravidla:** jadro je **jeden veľký balík** (schema registry, data API, rule engine, eventy, auth, rbac, realtime, automation, tenancy, audit, caching, Management API + defaulty portov ako vertical slices), plus balíky len tam, kde hranica platí. To vyjde na **rádovo ~10 balíkov pre v0.1, nie 30+**. Začni konzervatívne: vyčleniť namespace do balíka neskôr je lacné, zlúčiť priveľa balíkov späť je breaking change.

**Ilustračný príklad (nezáväzný — agent nech navrhne vlastný):**
- `MMLib.Alvo.Abstractions` (porty, bez závislostí) · `MMLib.Alvo` (jadro + builder)
- data providery ako samostatné balíky (každý ťahá driver): SQLite (dev), PostgreSQL, SQL Server
- `MMLib.Alvo.Admin` (Blazor — ťažká závislosť) · `MMLib.Alvo.Host` (Docker) · `MMLib.Alvo.Cli`
- `MMLib.Alvo.Testing` (contract suite + fakes) · `MMLib.Alvo.Templates`
- **neskôr, keď feature pristane:** Scripting (Roslyn), Functions.ContainerApps (Azure), Azure/Kubernetes bundle providerov, Aspire, Client codegen, Mcp adaptér — každý odôvodnený cudzou závislosťou. Konkrétne provider-adaptéry (SendGrid, S3…) sa pridávajú **na dopyt**, nie preventívne.

**Tvrdé pravidlá závislostí (tieto dodrž):** `Abstractions` nezávisí na ničom; jadro závisí len na `Abstractions`; **žiadny balík nezávisí na provideri iného portu**; verzovanie lockstep (SemVer, všetko vydané spolu jednou verziou).

### 1.2 Porty a ich garancie (čo musí existovať — nie ako to má vyzerať)

- [x] *prečítané*

> **Toto je zadanie, nie hotový návrh.** Nižšie je zoznam portov, ktoré framework potrebuje, a **garancie/invarianty**, ktoré musí každý splniť. **Konkrétne signatúry, názvy typov a tvar API navrhne agent** v Superpowers brainstorming/plan fáze — s reálnym kódom pred sebou to spraví lepšie než dokument fixujúci parametre mesiace vopred. Dôležité je *čo port garantuje*, nie ako sa volá jeho metóda.

**Dátové porty**
- **Schema registry** — poskytuje model entít. Dva zdroje: introspekcia fyzickej DB (physical) a metadata tabuľky pre dynamické entity (dynamic). *Garancia:* jeden model, dva drivery, volajúci nerozlišuje.
- **Data store** — CRUD + query nad entitami. *Garancia (tvrdá):* každá operácia dostáva volajúci kontext (identita, tenant, claims) a **policy sa vynucuje vnútri portu, nie „okolo" neho** — nedá sa obísť priamym volaním.

**Rule engine (bezpečnostné jadro)**
- Kompiluje podmienky (CEL podmnožina, princíp 6) do dvoch backendov: parametrizovaný SQL predikát a in-memory vyhodnotenie nad payloadom. *Garancie (tvrdé):* kompilácia **fail-fast** pri uložení (neexistujúci stĺpec = chyba hneď); autorizácia ide **do SQL WHERE, nikdy post-filter v pamäti**; užívateľský vstup sa **nikdy neinterpoluje** do SQL (property test to dokazuje).

**Lifecycle hooks**
- before/after per operácia, dve tváre (deklaratívna z descriptora + C# v embedded hoste) cez **jeden pipeline** (rovnaká sémantika). *Garancie (tvrdé):* before = in-transaction, časový rozpočet, **zákaz siete** (vynútené analyzerom/štrukturálne); môže reject (→ RFC 7807) alebo mutate. after = post-commit z outboxu, durable, retries, sieť povolená.

**Event systém (chrbtová kosť)**
- Publish–subscribe nad zmenami. *Garancie (tvrdé):* event sa publikuje **v tej istej transakcii** ako dátová zmena (transactional outbox — žiadny stratený ani fantómový event); subscribe podporuje wildcard vzory (`entity.orders.*`).

**Infraštruktúrne porty (provider model)** — každý má default v jadre a swap-bod:
- **Secret store** (get + rotácia/versioning), **object store** (put/get/list + presigned/Valet Key + deklarácia schopností), **cache store** (get/set + tag-based invalidácia), **email/sms/push sender**, **identity provider** (challenge/callback → jednotný model identity), **change feed** (voliteľný CDC hardening; primárny realtime je in-process outbox), **telemetry sink** (OTel).
- **AI connection** — swappable LLM provider cez jednotné rozhranie (Microsoft.Extensions.AI); lokálne (Ollama/OpenAI-kompatibilné) aj cloud; kľúč cez secret store; *garancia:* prepnutie providera = zmena connection, nie kódu.
- **Function runtime** — kde/ako beží custom logika; **dve nezávislé osi** (§2.7 analýzy): izolácia (in-process / sidecar / microVM) × spustenie (sync / queued cez outbox+bus). *Garancia:* offload do workera je nezávislý od typu izolácie.
- **Provider capabilities** — providery deklarujú schopnosti (napr. presigned upload, transactional outbox), framework degraduje kontrolovane, keď schopnosť chýba.

**Čo je otvorené pre brainstorming (agent rozhodne, nefixuj vopred):** presné signatúry a názvy typov; async model (ValueTask vs Task, IAsyncEnumerable kde); tvar query modelu; ako presne sa reprezentuje CompiledRule/SqlPredicate; členenie portov (napr. či je hooks jeden port alebo per-operácia). Rozhoduj podľa toho, ako veci sadnú na provider model a testovateľnosť.

**Kde sa inšpirovať (hint, nie príkaz):** row-level security a auto-API — PostgREST, Supabase; rule/ECA model — Directus; provider/capability model — ASP.NET Core storage/identity abstrakcie. **Nekopíruj — porovnaj a rozhodni** podľa princípov §0.

### 1.3 Ako sa to bude používať — ilustrácia zámeru DX (NIE záväzné API)

- [x] *prečítané*

> Nasledujúce ukážky **nie sú kontrakt** — ukazujú *ambíciu developer experience* (zero-config dev, jeden vstupný bod, plynulé škálovanie do produkcie a embedded). Presný tvar volaní (`AddAlvo`, fluent metódy, názvy) navrhne agent. Záväzné je len to, čo tieto ukážky *demonštrujú ako cieľ*: (1) dev beh bez konfigurácie do pár sekúnd, (2) jeden vstupný bod pre celý framework, (3) produkcia = pridanie providerov, nie prepis, (4) embedded = ten istý model vo vlastnom hoste.

**Standalone appka (dev, zero-friction):**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAlvo();          // bez konfigurácie = SQLite súbor, in-memory cache, konzolový email

var app = builder.Build();
app.MapAlvo();                        // Data API + Auth + Admin na /alvo (minimal API route groups)
app.Run();
// → dotnet run → funkčný backend do pár sekúnd, admin na /alvo/admin
```

**Produkcia (Azure):**

```csharp
builder.Services.AddAlvo(alvo => alvo
    .UseAzure()                                   // KeyVault, Blobs, Service Bus, App Insights, Entra ID
    .UseDatabase(db => db.UsePostgreSql(cs))      // alebo .UseSqlServer(cs)
    .UseTenancy(t => t.ResolveFromSubdomain())
    .UseAuth(a => a.AddGoogle().AddMicrosoft())
);
```

**Embedded v existujúcej appke (ERP scenár):**

```csharp
builder.Services.AddAlvo(alvo => alvo
    .Embedded(e => e.SchemaPrefix("alvo"))        // systémové tabuľky v alvo.* schéme, vlastný migračný reťazec
    .EnableDynamicEntities()                      // klienti si tvoria vlastné evidencie cez agenta
);
app.MapAlvo("/api/alvo");
```

**Schema-as-code (agent-friendly):**

```csharp
[AlvoEntity]
public class Order
{
    public Guid Id { get; set; }
    public string CustomerEmail { get; set; } = "";
    public decimal Total { get; set; }
    [AlvoDefault("draft")] public string Status { get; set; } = "draft";
}
// alebo deklaratívne v descriptore (JSON) — framework vygeneruje migráciu diffom
```

**Rules-as-code (autorizácia aj automation, jeden jazyk):**

**`alvo/entities/orders.rules.json`** — podmienky = CEL podmnožina (kompiluje sa do SQL predikátu):

```json
{
  "list":   "@user.id != null && tenant_id == @tenant.id",
  "update": "@user.role == 'editor' && owner_id == @user.id"
}
```

**`alvo/entities/orders.hooks.json`** — deklaratívne lifecycle hooky (standalone aj embedded). `condition` = CEL; `reject` zruší operáciu, `mutate` upraví payload pred zápisom:

```json
{
  "beforeUpdate": [
    { "condition": "old.status == 'closed'",
      "action": { "reject": "Uzavretú objednávku nemožno meniť" } },
    { "condition": "changed(total) && new.total < 0",
      "action": { "mutate": { "total": 0 } } }
  ],
  "afterCreate": [
    { "action": { "type": "entity.update", "entity": "stats",
                  "payload": { "orders_count": "+1" } } }
  ]
}
```

**`alvo/automation/notify-approved.json`** — `condition` = CEL; `payload` transformácia = JSONata (len after-side); `{{...}}` = cukor nad JSONata:

```json
{
  "trigger":   { "event": "entity.orders.updated" },
  "condition": "changed(status) && new.status == 'approved' && new.total > 1000",
  "actions": [
    { "type": "webhook", "endpoint": "erp-integration",
      "payload": "$merge([ new, { \"source\": \"alvo\" } ])" },
    { "type": "email", "template": "order-approved", "to": "{{new.customer_email}}" }
  ]
}
```
> Formát je JSON, jediný (JSON Schema, IntelliSense, agent-friendly). Žiadny YAML/JSONC.

---

## 2. Fáza 2 — README a dokumentácia použitia

- [x] *prečítané*

Cieľ: README, ktoré predá projekt aj vysvetlí použitie skôr, než existuje kód — slúži zároveň ako akceptačný test návrhu DX (ak sa README píše ťažko, návrh je zlý).

Obsah README (repo root):
- Pitch: slogan, 3 vety čo to je, pre koho (agentic dev + embedded ERP).
- **Quick start ≤ 10 riadkov** (dev-mode SQLite, `dotnet run`).
- Ukážky: schema-as-code, rules, automation, realtime klient, prepnutie na produkčnú DB.
- Tabuľka balíkov s jednovetovým popisom.
- Architektonický diagram (Mermaid): request → policy → data / event pipeline → outbox → automation/realtime/webhooky.
- Licencia (**Apache-2.0** pre jadro) + jasné vysvetlenie open-core modelu: jadro je a ostane zadarmo a open source; komerčné sú len enterprise doplnky (`Alvo.Enterprise.*`) a voliteľný hosting.
- Odkazy: doménová analýza, `llms.txt`, CONTRIBUTING.

K tomu: `docs/` skeleton (getting-started, concepts, providers, security model — trust boundary z §10.3 analýzy) a `llms.txt` od prvého dňa.

---

## 3. Fáza 3 — Základné testy (kontrakty pred implementáciou)

- [x] *prečítané*

Cieľ: spustiteľná test suite, ktorá definuje správanie skôr, než existuje implementácia. Balík `MMLib.Alvo.Testing`.

**Testovací stack (záväzný — jeden pohľad):**

**Runtime = Microsoft.Testing.Platform (MTP), nie starý VSTest.** Nový projekt na .NET 10 → MTP je default a zrelý (všetky hlavné frameworky majú MTP runnery). Framework = **xUnit v3** (má MTP podporu zabudovanú natívne; stabilný, 1.0+, agent ho pozná dokonale). TUnit zvážený a odložený — je pred 1.0, API sa môže meniť; prehodnotiť po jeho 1.0. Dôsledok pre CI: `dotnet test` cez MTP, **nie VSTest task** (viď X.3).

| Typ testu | Nástroj | Účel | Kedy beží |
|---|---|---|---|
| Unit / integračné | **xUnit v3 na MTP** | základ všetkého | každý PR |
| Assertions | Shouldly (alebo AwesomeAssertions) — pozor: FluentAssertions v8+ je komerčný (§14) | čitateľné asserty | každý PR |
| Fakes / test doubly | NSubstitute | mockovanie portov | každý PR |
| Property-based | CsCheck (príp. FsCheck) | CEL→SQL invarianty, round-trip descriptora, **API invarianty naprieč projektmi** | každý PR |
| Snapshot | Verify (+ Verify.HeadlessBrowsers pre admin UI) | OpenAPI, CEL→SQL, RFC 7807, migrácie, descriptor→model | každý PR |
| Public API approval | PublicApiGenerator + Verify (alebo Roslyn PublicApiAnalyzers) | breaking-change gate na Abstractions | každý PR |
| Architektúra | NetArchTest.Rules | pravidlá závislostí balíkov (napr. Abstractions nezávisí na ničom) | každý PR |
| Integračné (DB) | Testcontainers (Postgres, SQL Server) | reálny engine, nie in-memory | každý PR (matrix) |
| **API contract linting** | **Vacuum** (Go binárka, Spectral-kompatibilné rulesety) | tvarové pravidlá OpenAPI: camelCase, operationId, konzistentný error shape, paginácia | každý PR (proti OpenAPI dema) |
| **API invariant testy** | xUnit + generované descriptory + Vacuum | *správanie* naprieč projektmi: idempotencia, default-deny, konzistentný CRUD (viď nižšie) | každý PR |
| Mutation | Stryker.NET | len bezpečnostné jadro — meria kvalitu adversarial suite | PR (path-filtered na zmenený security-core) |
| E2E (admin UI) | **Playwright (.NET — xUnit + Microsoft.Playwright)** | Blazor dashboard ako reálny používateľ | PR (celé) |
| E2E (API, demo) | TeaPie (Kros-sk) | čierna skrinka nad bežiacim demo API | pred publish image |

Princíp: **contract + snapshot + public-API + arch + API linting bežia rýchlo v každom PR; integračné cez Testcontainers v PR (affected-scoped cez `dotnet-affected`); mutation (path-filtered) a e2e (Playwright/TeaPie, celé) tiež bežia v PR — pomalšie, ale PR je jediná plná brána, žiadny nightly ani post-merge beh.**

1. **Contract testy per port** — jedna abstraktná testovacia trieda na rozhranie (`ObjectStoreContractTests`, `CacheStoreContractTests`, …). Každý provider ju zdedí a musí prejsť celou sadou. Prvá implementácia, ktorá ich plní: in-memory fakes (tie sú zároveň produkt — používatelia ich dostanú na testovanie svojich appiek).
2. **Expression language testy** — CEL parser + kompilácia do SQL: property-based testy (CsCheck/FsCheck), že preklad nikdy neinterpoluje užívateľský vstup; golden testy syntaxe (CEL podmnožina + rozšírenia changed/old/new/@user); fail-fast na neexistujúci stĺpec; JSONata evaluátor: depth/time limity, zákaz in-transaction (testom).
3. **Architektúrne testy (NetArchTest)** — vynútenie pravidiel závislostí z §1.1 ako testy: `Abstractions` nezávisí na ničom, jadro `MMLib.Alvo` len na `Abstractions`, žiadny balík nezávisí na provideri iného portu, vertical-slice namespace konvencie. Chyba architektúry = červený test, nie code review.
4. **Integračné testy (Testcontainers)** — data vrstva proti reálnemu Postgres a SQL Serveru v kontajneri (nie len in-memory fake): migrácie, generated columns, keyset pagination, transactional outbox. Odhalí engine-špecifické rozdiely, ktoré in-memory fake skryje.
5. **Adversarial suite (kostra)** — two-user testy (user A nikdy nevidí dáta usera B), two-tenant testy, default-deny testy. V tejto fáze červené/skipped — definujú latku pre fázu 4.
6. **Snapshot testy (Verify)** — zmrazenie generovaných artefaktov, kde diff = zmena kontraktu: vygenerované OpenAPI, **preklad CEL → SQL predikát** (per engine SQLite/Postgres/SqlServer — odhalí neúmyselnú zmenu translatora), RFC 7807 chybové odpovede, vygenerované migrácie z deklaratívnej schémy, descriptor → interný model mapovanie.
7. **Public API approval testy** — `PublicApiGenerator` + Verify (alebo Roslyn `PublicApiAnalyzers` so `PublicAPI.Shipped/Unshipped.txt`) nad `MMLib.Alvo.Abstractions` a všetkými verejnými balíkmi: PR meniaci verejnú signatúru rozbije test a v diffe presne ukáže zmenu. Priame previazanie na SemVer disciplínu — nútené vedomé potvrdenie breaking change (major bump), nie prehliadnutie. Kľúčové, lebo Abstractions je kontrakt voči providerom aj embedded hostom (§2.13 analýzy).
8. **Mutation testy (Stryker.NET)** — cielené **len na bezpečnostné jadro**: rule engine (mutácia operátora v SQL preklade musí zabiť test), expression evaluátor, tenant izolácia. Meria, či adversarial suite naozaj chytá diery, nie len zvyšuje coverage. Na boilerplate providerov sa nepúšťa (drahé, málo hodnoty).
9. **Infra:** Testcontainers (Postgres, SQL Server, MinIO, Redis), CI matrix SQLite × PostgreSQL × SqlServer od prvého dňa (poučenie: EF Core migrácie sa líšia per provider — testovať všetky tri, nie jeden).
10. **API contract linting (Vacuum)** — Alvo generuje OpenAPI; Vacuum ho zlintuje proti vlastnému rulesetu (camelCase názvy, každá operácia má `operationId`, konzistentný error shape = RFC 7807, paginácia má správne parametre, žiadne integery v URL). Go binárka v CI (žiadny Node), Spectral-kompatibilný ruleset (`.vacuum`/`ruleset.yaml`). Beží proti **OpenAPI dema** ako referenčnému bodu. **Čo chytá:** tvarové/naming/štrukturálne pravidlá. **Čo NEchytá:** či sa API tak naozaj správa — na to sú API invariant testy.
11. **API invariant testy** — dôkaz, že invarianty platia **naprieč rôznymi descriptormi, nie len pre demo**. Test vytvorí za behu N rôznych projektov (rôzne entity, typy polí, vzťahy — generované, príp. property-based cez CsCheck), naštartuje API a pre každý overí: (a) vygenerované OpenAPI prejde Vacuum lintom, (b) **idempotencia** — dvakrát rovnaké PUT = ten istý stav; opakované create s Idempotency-Key nevytvorí duplikát, (c) **default-deny** platí bez explicitnej policy, (d) CRUD je tvarovo konzistentný (rovnaké error kódy, paginácia, RFC 7807). Chytá „funguje len pre demo, rozbije sa pri inej kombinácii polí" — typická diera metadata-driven frameworku. *Statické tvarové pravidlá rieši Vacuum; behaviorálne (idempotencia, default-deny) sa dajú overiť len voči bežiacej inštancii — preto oboje.*
12. **Admin E2E (Playwright .NET)** — Blazor dashboard cez `Microsoft.Playwright` (xUnit): first-run wizard, vytvorenie projektu/entity, schema editor → export descriptora, policy simulator, AI-navrhnutá zmena → potvrdenie diffu. Verify.HeadlessBrowsers na vizuálne snapshoty kľúčových obrazoviek. Pomalé, ale beží v PR (nie nightly) — celé, nie affected-scoped.

**Rýchlosť CI:** PR je jediná plná brána (žiadny nightly/post-merge). Rýchle vrstvy (contract, snapshot, public-API, arch) bežia celé; integračné sú v PR **affected-scoped** cez `dotnet-affected` (len zmenení provideri); mutation je path-filtered na zmenený security-core; e2e celé. Všetko zelené = podmienka merge, ktorý robí len človek.

Definition of done: `dotnet test` beží v CI, contract testy zelené proti fakes, adversarial suite existuje ako spustiteľná (aj keď zatiaľ skipped proti reálnym providerom).

---

## 4. Fáza 4 — Základná funkčnosť (CRUD)

- [x] *prečítané*

Cieľ: prvý reálne použiteľný vertikálny rez — **Data API + rule engine + eventy**, na SQLite aj PostgreSQL. **SQL Server/Azure SQL provider (`Data.SqlServer`) sa zapína hneď po zelenej Postgres sade ešte v rámci F4** — CI matrix beží na troch enginoch od F3, takže SqlServer je len tretí cieľ tej istej contract/adversarial sady, nie nová fáza.

Rozsah (mapuje §2.1 + §2.4 + §3.2 analýzy):
- Schema registry (physical driver: EF Core introspekcia; migrácie diffom z deklaratívnej schémy).
- **Migrácie — jeden diff engine, dva zdroje želaného stavu (§2.13 analýzy).** Code-first (descriptor ako súbor v repe, diff pri builde) **aj** runtime/dashboard-first (descriptor ako záznam v DB, menený za behu cez Management API nad reálnymi dátami) idú **tým istým declarative-diff engine**, nie dvomi systémami. Runtime cesta navyše vyžaduje: **verzovanie descriptora v DB** (append-only história = audit + rollback, náhrada za git), **rollback** cez generovanú spätnú migráciu (guardrail na DROP, lebo dáta sú preč), a **optimistic locking** na descriptore (dvaja admini naraz = konflikt, nie git merge). Deštruktívne zmeny majú rovnaké guardrails na oboch cestách (v runtime o to prísnejšie — živé dáta). Export runtime→súbor (§9.2 most) drží dashboard-first ako neslepú uličku.
- REST CRUD: list/get/create/update(PATCH)/delete/upsert; filtre (allow-listované operátory, PostgREST-kompatibilná syntax), sort, projekcie, offset + keyset pagination, RFC 7807 chyby, Idempotency-Key.
- Rule engine: kompilácia pravidiel do SQL predikátov, per-operation rules, field-level hidden/read-only, default deny.
- Event pipeline: transactional outbox, `entity.*.created|updated|deleted` s `record`/`old_record`/changed-columns, in-process subscriber. *(Hint: Wolverine má outbox aj mediator; over či sadne, alebo vlastný.)*
- **Automation minimal** (predpoklad pre F5 admin builder a F6 demo): ECA vyhodnotenie nad outbox eventami (trigger `event` + CEL condition + akcie `webhook`/`email`/`entity.update`/`function`), **schedule trigger** (cron, UTC) a základný `IEmailSender` (konzolový pre dev + SMTP provider). Bez HMAC/DLQ/redelivery UI — to je rozšírenie 7.1. Demo pravidlo „STK končí o 30 dní → email" musí byť postaviteľné už z F4.
- Lifecycle hooks pipeline: before/after per operácia — deklaratívna tvár (descriptor: condition + reject/mutate/akcie) aj C# tvár (`IEntityHooks`), obe cez jeden pipeline; before in-transaction s rozpočtom, after z outboxu.
- Minimálna auth pre vývoj: API key + service key (plné auth flows prídu vo fáze 7.4).
- **Validácia v API vrstve, odvodená zo schémy:** required/dĺžky/typy/formáty/enum/FK sa kontrolujú v minimal API pipeline pred perzistenciou (400 + RFC 7807 so zoznamom porušení); DB constraints sú len defense-in-depth poistka, nie primárne miesto validácie. Pri dynamických entitách (§2.1) je aplikačná validácia podľa `field_definitions` jediná vrstva.
- **Počítané polia — dva deklaratívne druhy + rozhodovací rebríček (§2.1):** `computed` = aritmetika/výraz nad poľami toho istého riadku (`total = unit_price * amount`) → **DB stored generated column** (Postgres/SQL Server/SQLite), pole read-only, dopočíta databáza. `rollup` = agregácia cez súvisiace záznamy (`sum(items.line_total)`) → **transakčne konzistentný** rollup (DB trigger alebo in-transaction prepočet), nie ručný hook — súčet detí na rodičovi je klasický race condition. Hranica: čokoľvek podmienené (napr. `closed_at` pri `stage==won`) = before-hook `mutate`; kontextová/časová logika (DPH) = hook/funkcia, nie výraz; zložité vetvenie a externé volania = automation akcia alebo csx. Poradie deklaratívne → hook → akcia → csx je ten istý extensibility gradient (§10).
- **Organizácia:** minimal API route groups + vertical slice per feature (endpoint+handler+validátor+model spolu); handlery cez DI (prípadne Wolverine mediator), **nie MediatR**.

Definition of done: adversarial suite z fázy 3 **zelená** na SQLite, Postgres aj SQL Serveri; crash test outboxu (kill process → žiadny stratený event); p95 latencie zmerané a publikované (kalibrácia akceptačných kritérií z analýzy).

---

## 5. Fáza 5 — Admin režim

- [x] *prečítané*

Cieľ: vizuálne rozhranie nad fázou 4 (§2.8 analýzy). Technológia: Blazor (jeden jazyk s jadrom) hostovaný ako middleware v `MMLib.Alvo.Admin`.

Technológia: **Blazor Web App (.NET 10)**, server interactive default (WASM ako voľba). **Tvrdé:** vlastný moderný design systém — **nie default Bootstrap/Blazor look**; mobile-first, plne responzívne (použiteľné z telefónu, tabuľky → karty, touch ciele), command palette (⌘K), data grid s virtualizáciou. *Hint:* pretémovať existujúcu komponentovú knižnicu (MudBlazor/Radzen/FluentUI) alebo vlastné — rozhodni podľa toho, či sa dá dosiahnuť vlastný vzhľad bez boja s knižnicou.

Rozsah: first-run wizard (prvý admin), správa projektov (standalone: DB per projekt), **export/import project descriptora** (dashboard-first: zdroj pravdy je schéma v DB inštancie; export ju dostane do repa, import naopak — most k GitOps ceste), schema editor **generujúci migrácie/descriptor**, data browser s auditom zásahov, rules/automation builder + **policy simulator**, csx editor s diagnostikou (UI-edit gate), webhook delivery log + redelivery, API docs (Scalar), RBAC dashboardu.

**AI agent v dashboarde (Microsoft Agent Framework):** konfigurovateľné AI connection (lokálne Ollama / cloud Azure OpenAI, OpenAI, Anthropic — cez `IAiConnection` port, kľúče v `ISecretStore`); agent navrhuje schému/rules/automation z prirodzeného jazyka, operuje cez to isté Management API ako CLI (MCP je len voliteľný adaptér nad ním), každý zásah auditovaný a cez RBAC; navrhuje → človek potvrdzuje (diff), deštruktívne operácie vyžadujú schválenie; dashboard funguje aj bez nakonfigurovaného AI.

Definition of done: všetko naklikateľné v UI (aj AI-navrhnuté) je exportovateľné ako kód (žiadny config drift); policy simulator odpovedá identicky ako produkčné vynucovanie (testom); dashboard plne ovládateľný na 375 px bez horizontálneho scrollu; vizuálny audit — neprejde ak vyzerá ako default template; prepnutie AI providera (lokálny ↔ cloud) je len zmena connection, kľúč v secret store; AI-navrhnutá zmena sa aplikuje len po potvrdení diffu, auditovaná; **kľúčové flows pokryté Playwright E2E** (first-run wizard → projekt → entita → export descriptora; policy simulator; AI návrh → potvrdenie diffu).

---

## 6. Fáza 6 — Demo

- [x] *prečítané*

Cieľ: dôkaz celého zámeru — **agent postaví backend jedným promptom**.

1. **Demo aplikácia** „Evidencia vozidiel" (vozidlá, majitelia, STK): schéma + rules + automation pravidlo („STK končí o 30 dní → email") + jednoduchý SPA frontend vygenerovaný agentom nad Alvo API.
2. **Demo scenár (bez MCP):** agent (Claude Code / Cursor) dostane prompt „vytvor evidenciu vozidiel s notifikáciou pred koncom STK" → vygeneruje **descriptor** proti JSON Schéme → `alvo apply` → funkčný backend bez ručného kódu. **Voliteľný MCP adaptér** (`MMLib.Alvo.Mcp`) dá tú istú schopnosť externému agentovi nad *bežiacou* inštanciou — minimálna sada `get_schema`, `apply_schema_change`, `list_entities`, `query`, `upsert_rule`, `get_logs`; je to nadstavba nad Management API, nie podmienka dema.
3. **Descriptor demo (režim 1):** ten istý backend „Evidencia vozidiel" ako project descriptor — `docker run -v vozidla.alvo.json:... mmlib/alvo` naštartuje hotový backend; a `alvo apply vozidla.alvo.json` to isté cez CLI.
4. **End-to-end API testy demo cez TeaPie** (Kros-sk/TeaPie) — `.http`/`.tp` súbory uložené vedľa demo backendu, pokrývajú reálny scenár nad bežiacim demo „vozidlá" API: auth flow, CRUD, overenie rule enforcement (obchodník nevidí cudzie záznamy), trigger automation → webhook. Dogfooding vlastného KROS nástroja; JUnit XML report do CI. Slúži zároveň ako živá dokumentácia API a ako smoke test pri každom release image.
5. Video/gif do README + blog post (.NET Insights) + `dotnet new alvo-app` šablóna.

Definition of done: demo scenár prejde end-to-end na čistom stroji (git clone → dotnet run → agent → funkčná appka) a je zreprodukovateľný podľa README.

---

## 7. Fáza 7+ — Ďalšia funkčnosť (poradie podľa hodnoty)

- [x] *prečítané*

Väčšina týchto komponentov je **vertical slice vnútri jadra `MMLib.Alvo`**, nie samostatný balík (viď hranica balíka v §1.1). Vlastný balík dostávajú len tie, čo ťahajú cudziu závislosť — v tabuľke označené **[balík]**. Každá položka: contract testy vopred, rovnaký vzor ako fázy 1–4.

| Poradie | Komponent | Balík? | Rozsah (odkaz na analýzu) |
|---|---|---|---|
| 7.1 | Automation (rozšírenie F4 základu) | slice | Outbound webhooky ako produkt: HMAC/Standard Webhooks, retries+DLQ, redelivery UI, inbound webhook trigger (§3.4). ECA jadro, cron a základný email existujú od F4 |
| 7.2 | Scripting | **[balík]** `MMLib.Alvo.Scripting` | csx runtime: compile-on-load + ALC + AlvoScriptContext, HTTP/event/schedule triggery, UI-edit gate (§2.7). Balík, lebo ťahá Roslyn |
| 7.3 | Functions (runtime) | slice + **[balík]** executor | `IFunctionRuntime` (orchestrácia + executor) v jadre; microVM executor `MMLib.Alvo.Functions.ContainerApps` je balík (Azure) (§2.7) |
| 7.4 | Auth (plné) | slice | Email/heslo, magic links, OAuth providery, refresh rotation + reuse detection, anonymous upgrade, OIDC relying party (§2.2) |
| 7.5 | RBAC | slice | Roly, tímy, permissions, delegovaná administrácia (§2.3) |
| 7.6 | Realtime | slice | SignalR subscriptions (filtered, presence, broadcast), authz na push, filter-boundary synthetic eventy (§2.5) |
| 7.7 | Storage | slice + provider balíky na dopyt | IObjectStore, TUS, presigned (Valet Key), orphan reconciliation (§2.6); filesystem default v jadre, S3/AzureBlobs balíky na dopyt |
| 7.8 | Tenancy | slice | **v0.1: len shared DB + shared schema (row-level cez rule engine — tá istá mašinéria ako §2.4 autorizácia, skoro zadarmo).** Porty (tenant resolution pluggable, tenant-aware data access, tenant-aware cache/events/storage, default-deny) navrhnuté tak, aby DB-per-tenant bol neskôr **doplnenie stratégie, nie prepis**. Schema-per-tenant vyhodené (§4). DB-per-tenant + migrácia shared→DB-per-tenant až pri reálnej potrebe |
| 7.9 | DynamicEntities | slice | Metadata-driven evidencie pre ERP scenár (§2.1) — závisí na 7.8 (Tenancy) |
| 7.10 | Audit | slice | Append-only stream, hash chaining, GDPR export/erasure (§5) |
| 7.11 | Messaging (plné), Caching, M2M+OpenAPI (§6), Client codegen | slice + provider balíky na dopyt | Základný SMTP/konzolový sender je od F4 v jadre; SendGrid/ACS/Twilio/FCM sú provider balíky na dopyt. Caching slice. **M2M auth: v0.1 len PAT (scoped, expirácia, revokácia, last-used — žiadny OAuth server); OAuth 2.1 client credentials + OpenIddict až neskôr do pripraveného token/scope portu.** OpenAPI 3.1 publish + Scalar (v0.1, skoro zadarmo); **SDK codegen (Kiota/NSwag) a `MMLib.Alvo.Client` až na dopyt — integrátor si klienta vygeneruje sám z publikovaného OpenAPI**; developer portál a API verzovanie až keď sú externí integrátori; dedikovaný sandbox mimo rozsahu (self-host + triviálny test projekt to pokryje) |

---

## X. Prierezové — od začiatku, nie na konci

- [x] *prečítané*

**X.1 Docker images**
- `mmlib/alvo` — standalone kontajner (`MMLib.Alvo.Host` + admin + Management API), multi-arch (amd64/arm64), `docker run -p 8080:8080 mmlib/alvo` = funkčný backend so SQLite volume; env-based infra konfigurácia + mount project descriptora pre bootstrap hotového backendu (režim 1, kap. 0.5).
- `docker-compose.yml` v repe: alvo + postgres + minio + mailhog = plné lokálne prostredie jedným príkazom.
- Zavádza sa už vo **fáze 4** (hneď ako existuje čo spustiť); CI publikuje image pri každom release.

**X.2 .NET Aspire — orchestrácia a deploy (Kubernetes, Azure)**
- `MMLib.Alvo.Aspire` hosting integrácia: `builder.AddAlvo()` v AppHost — Alvo ako Aspire resource s health checks, OTel telemetriou do Aspire dashboardu a service discovery (DB, Redis, MinIO ako Aspire resources).
- Deploy cesty z jedného Aspire app modelu: **Azure** cez `azd up` (Container Apps; KeyVault/Blobs/Service Bus previazané automaticky na Alvo porty) a **Kubernetes** cez Aspire → manifest generovanie (Aspir8/aspirate) s Helm chart ako artefakt.
- Aspire dashboard = lokálna observability zadarmo (traces request → rule → outbox → webhook viditeľné bez setupu) — priamo plní §2.11 analýzy pre dev loop.
- Zavádza sa po fáze 6 (demo beží aj cez `aspire run`), deploy recepty (azd, Helm) ako súčasť dokumentácie fázy 7.

**X.3 Ostatné prierezové**
- CI/CD: GitHub Actions — build, test matrix (3 DB enginy), pack, publish NuGet + Docker; `dotnet format` + analyzery + **public API approval** ako gate (breaking change verejného API zastaví merge); snapshot (Verify) v PR. **Testy cez MTP:** `dotnet test` v MTP režime (nie VSTest task — ten MTP testy neobjaví správne); TRX/JUnit report cez `--results-directory` explicitne (cesta sa líši od VSTest defaultov).
- **PR je jediná plná brána — žiadny nightly, žiadny post-merge beh.** V PR bežia **všetky** testy vrátane mutation a e2e, aj keď to trvá minúty. Nič sa neodkladá „na noc" ani „po merge do main" — rozbitý main nájdený neskoro je presne to, čomu sa vyhýbame. Priamy push do `main` je zakázaný (branch protection); merge robí len človek po review.
- **Zrýchlenie cez `dotnet-affected`, nie odkladom:** affected-scope na **integračné testy** (Testcontainers) — v PR sa spustia len pre zmenených providerov + ich závislých (MSBuild ProjectGraph + git diff). Unit/contract/arch a e2e bežia **celé vždy** (rýchle, resp. celosystémové — affected pri nich nepomáha a riskuje out-of-graph dieru). Affected je zrýchlenie *v rámci PR*, nie výhovorka odložiť testy.
- **Vacuum v pipeline:** krok `vacuum lint` proti vygenerovanému OpenAPI dema proti projektovému rulesetu — gate v každom PR, ktorý sa dotýka API/schémy. Go binárka, žiadny Node v pipeline.
- **Stryker.NET v pipeline:** mutation run nad security-core **v každom PR**, path-filtered len na zmenené jadro/rule engine/tenancy (na nedotknutom kóde nič nezistí a len zdržuje), s prahom mutation score ako gate. Beží cez MTP `dotnet test`.
- **Playwright (admin E2E) v pipeline:** **v PR** — nainštaluje browsery (`playwright install`), spustí Blazor dashboard, prejde kľúčové flows. Pomalé, ale beží v PR (nie nightly); celé, nie affected-scoped (celosystémové).
- **TeaPie v pipeline:** po build+spustení demo image krok `teapie test` proti bežiacemu kontajneru (docker-compose alebo Aspire), JUnit XML report do CI; e2e smoke gate pred publikovaním Docker image. Beží v PR dotýkajúcom sa demo/API vrstvy a pri release.
- **Lokálne kruhy (agent):** `scripts/test-ring0` (unit, priebežne) → `test-ring1` (+ arch/public-API, po slice) → `test-ring2` (+ integračné affected-scoped + API invariant + Vacuum, pred PR). Plný beh (+ mutation + e2e) rieši CI v PR, nie lokálny skript. Rýchlosť kruhu určuje frekvenciu.
- Licencia **Apache-2.0** (jadro) v repe od prvého commitu (nie dodatočne — Directus/MassTransit lekcia). Open-core: jadro permisívne navždy; enterprise doplnky a hosting sú komerčné, ako samostatné balíky/repo `Alvo.Enterprise.*`. Prípadná neskoršia komercializácia = pridanie doplnkov, NIE prelicencovanie jadra.
- CHANGELOG + SemVer disciplína od v0.1.

---

## Súhrnná mapa fáz

- [x] *prečítané*

```
F1 Rozhrania + balíky ──► F2 README ──► F3 Contract testy ──► F4 CRUD jadro ──► F5 Admin ──► F6 Demo (descriptor+agent; MCP voliteľný) ──► F7+ komponenty
                                              │                    │
                                              └── fakes (produkt)  └── X.1 Docker (od F4) ── X.2 Aspire (od F6)
```

Míľnik „verejné v0.1" = koniec fázy 6: rozhrania stabilné, CRUD + rules + eventy + admin + descriptor demo (MCP adaptér voliteľný), Docker image, README — presne toľko, aby prvý externý používateľ (alebo agent) postavil reálnu appku.

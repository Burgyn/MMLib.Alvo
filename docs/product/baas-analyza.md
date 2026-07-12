> **Názov — Alvo**

# *Alvo* — Backend-as-a-Service pre .NET, natívne pre agentický vek

„Your intent, running in production."

Hĺbkový materiál pre architekta plánujúceho open-source BaaS natívny pre .NET. Každý komponent (vrátane caching, multi-tenancy, audit, telemetry, M2M integrácií) rozobraný ako **swappable provider** v štruktúre: čo to je, čo musí obsahovať, štandard, na čo si dať pozor, akceptačné kritériá — podložené researchom reálnej konkurencie.

## §0 Zámer a ciele — prečo tento BaaS vzniká

Vstupujeme do agentického veku vývoja softvéru: aplikácie čoraz častejšie nevznikajú písaním kódu, ale **konfiguráciou cez coding agentov** (Claude Code, Cursor a nasledovníci). Frontend už agenti generujú dobre — SPA frameworky majú obrovské zastúpenie v tréningových dátach a výsledok je okamžite viditeľný. Úzkym hrdlom zostáva backend: auth, autorizácia, dáta, eventy, storage, integrácie — veci, kde chyba nie je kozmetická, ale bezpečnostná či dátová. Tento BaaS existuje preto, aby backend prestal byť úzkym hrdlom.

**Alvo** pochádza z latinského *alveus* — koryto, nosné lôžko, dutina, ktorá niečo drží a vedie. Presne to robí BaaS: je to lôžko, v ktorom tečú dáta a na ktorom stojí aplikácia. Vývojár (či agent) definuje zámer, Alvo nesie celý backend pod ním. Druhé čítanie: v portugalčine a španielčine *alvo* znamená „cieľ / terč" aj „čistý" — čistý štart a jasný cieľ namiesto inštalatérčiny okolo. Dve slabiky, ľahko vysloviteľné slovensky aj anglicky. NuGet balíky nesú etablovaný prefix **MMLib.*** (Milan Martiniak Libraries — rovnaká rodina ako MMLib.SwaggerForOcelot a MMLib.OpenApiForYarp): `MMLib.Alvo` (jadro), `MMLib.Alvo.Data.PostgreSql`, `dotnet add package MMLib.Alvo.Admin`. Prefix zároveň odlišuje projekt od nesúvisiacich firiem s menom „Alvo" (viď poznámka nižšie).

**Dostupnosť názvu (stav k researchu, nie právne stanovisko):** v .NET / NuGet / dev-tool priestore je „Alvo" voľné — žiadna knižnica ani framework s týmto menom, prefix `MMLib.Alvo` je čistý. Ako *firemné / TM meno* je „Alvo" obsadenejšie: existuje Alvotech (biotech, Nasdaq ticker ALVO), Alvo Trading Software B.V., alvo.gr (tech/AI) a ALVO Medical. Pre open-source knižnicu pod MMLib prefixom to nevadí; pred registráciou ochrannej známky pre budúci komerčný SaaS produkt odporúčam TM previerku u právnika. Ber ako žltú, nie zelenú.

**Backronym:** *Alvo = **A**pplication **L**ayer for **V**ision & **O**perations* — vložíš víziu (zámer), Alvo z nej spraví bežiace operácie v produkcii. Sekundárne k primárnemu etymologickému významu (alveus = nosné lôžko), ako bonus.

**Slogan:** *„Your intent, running in production."*  ·  Alternatívy podľa kontextu: *„Describe the intent. Alvo is the backend."*  ·  *„The backend, minus the backend work."* (bez-programovania)  ·  *„The .NET-native foundation your app sits on."* (.NET a metafora lôžka).

> **Primárny cieľ**
**Veľmi rýchlo vytvoriť plnohodnotný, robustný, moderný backend bez reálneho programovania.** Cieľový workflow: človek zadá zámer, *backend agent* ho zrealizuje konfiguráciou tohto BaaS (schema-as-code, rules-as-code, automation rules — §9), *frontend agent* vygeneruje UI v SPA frameworku nad auto-generovaným API. Výsledok nie je prototyp, ale produkčný backend: s autorizáciou vynucovanou v dátovej vrstve (§2.4), auditom (§5), eventmi a webhookmi (§3), multi-tenancy (§4) — teda so všetkým, čo by inak tím staval mesiace. Preto je celý dokument písaný cez „Musí obsahovať / Akceptačné kritériá": latka je *robustný* backend, nie demo.

> **Sekundárny cieľ — embedded v SaaS ERP**
Ten istý engine, zabudovaný ako NuGet knižnica do modernej SaaS ERP platformy, umožní **koncovým klientom vytvárať si cez AI agenta vlastné „evidencie" a funkčnosti** — evidenciu vozidiel, zmlúv, majetku — bez zásahu vývojárov platformy a bez vzniku novej databázy či tabuľky per evidencia (metadata-driven dynamické entity, §2.1; tenant izolácia, §4). Primárny a sekundárny cieľ nie sú dva produkty: je to jeden framework v dvoch primárnych režimoch: **Standalone** (Docker image + dashboard + project descriptor) a **Embedded** (NuGet vo vlastnom hoste, plná C# extensibility) — detailne §2.14 — a práve táto dvojrola diktuje viaceré architektonické rozhodnutia v dokumente (provider model §1, engine-agnostické jadro §8.2, schema-at-runtime §2.1).

> **Prečo .NET**
Celé na .NET stacku, ktorému rozumieme: jazyk a runtime, v ktorom máme hlbokú kompetenciu, existujúcu kódovú bázu, tooling aj ľudí. Zároveň je to trhová medzera (.NET-native BaaS neexistuje, §12.1) a štrukturálna výhoda: host, custom logika, pluginy aj escape hatch sú ten istý jazyk (§10), a C# compile-time bezpečnosť je pre agentmi generovaný kód silnejšia záruka než čokoľvek runtime-ové (§9.2).

> **Licenčný model — Apache-2.0 jadro + open-core financovanie**
Jadro bude **Apache-2.0** — skutočný OSI-schválený open source: ktokoľvek si ním môže postaviť backend pre seba aj pre klientov, vrátane komerčného použitia, a licencia sa nikdy nezmení. Financovanie stojí na **open-core modeli** (Sidekiq/Supabase/Grafana): jadro je zadarmo a kompletné pre jednotlivcov aj malé tímy; komerčné sú len jasne *enterprise* doplnky (SSO/SAML, jemnozrnný RBAC, audit, HA, dlhá retencia — balíky `Alvo.Enterprise.*`) a voliteľný manažovaný **Alvo Cloud**. Prečo nie source-available non-compete (FSL/BUSL) na celom projekte: (1) nie je to OSI „open source" — časť enterprise firiem má policy len na OSI licencie, čo by brzdilo adopciu ešte pred vznikom komunity; (2) v §13.3 sami kritizujeme licenčné míny — permisívne jadro drží konzistenciu, non-compete klauzula by pôsobila protirečivo; (3) kľúčové poučenie z Terraform/Elastic/Redis/FluentAssertions je, že hejt spúšťa *odobranie práv, ktoré ľudia už mali* — Apache-2.0 jadro toto riziko odstraňuje, lebo prípadná neskoršia komercializácia je *pridanie* doplnkov, nie prelicencovanie. Non-compete ochranu (FSL na úzkej hostovanej/orchestračnej vrstve) možno pridať neskôr, ak by tretia strana reálne chcela prepredávať Alvo-as-a-service — dovtedy je to zbytočné trenie. Štruktúrne poistky od prvého dňa: rozdelenie na `Alvo.*` (jadro) vs `Alvo.Enterprise.*` balíky, ľahký **CLA** (aby ostala možnosť dual-licence bez zháňania súhlasu prispievateľov) a **ochranná známka „Alvo"** (chráni značku a „Alvo Cloud" aj pri plne otvorenom kóde). Detailná analýza modelov a citlivá komunikácia: viď samostatný dokument o licencovaní a monetizácii.

**Delayed open source ako odpoveď na lock-in (§10.3):** keďže jadro je rovno Apache-2.0, obava „čo ak autor zmizne" je bezpredmetná — kód je slobodný hneď, netreba čakať na 2-ročnú konverziu ako pri FSL.

> **Ako z toho vyplýva zvyšok dokumentu**
- Agent je primárny „používateľ" → AI-readiness (§9) nie je feature, ale návrhový princíp každého komponentu (deklaratívna konfigurácia cez descriptor, štruktúrované chyby, idempotencia; MCP je voliteľný adaptér nad API).
- „Bez reálneho programovania" neznamená „bez možnosti programovať" → extensibility gradient (§10) od computed fields po plný C#.
- ERP embedding → dynamické entity (§2.1), multi-tenancy (§4), kohabitácia schémy (§2.13), provider model (§1).
- „Robustný" → akceptačné kritériá, audit (§5), secure-by-default (§2.4), enterprise požiadavky (§5, §6).

- **.NET-native open-source BaaS neexistuje.** Odporúčanie: PostgreSQL základ, C#-first extensibility, agent-first dizajn (descriptor + schema-as-code; MCP len voliteľný adaptér), multi-tenancy first-class, a — kľúčové doplnenie tejto verzie — **unified event system s automation engine a webhookmi ako chrbtová kosť celého frameworku** (nie feature prilepená na konci).
- **Automation je najpodceňovanejší diferenciátor.** Supabase má len surové DB webhooky (pg_net), Appwrite events nevedia filtrovať na zmenu konkrétneho atribútu (dokumentovaný pain point), jedine Directus Flows majú plný event–condition–action model. Nikto nemá poriadne: transactional outbox, Standard Webhooks signing, coalescing pri bulk operáciách a jednotný expression language pre pravidlá aj autorizáciu.
- **Licenčné míny .NET ekosystému:** stavaj len na permisívnych blokoch (Npgsql, OpenIddict, SignalR, HotChocolate, tusdotnet, Wolverine, Hangfire); vyhni sa MassTransit v9, ImageSharp, Duende, MediatR/AutoMapper.
- **Každý infraštruktúrny komponent musí byť swappable provider.** Framework definuje rozhranie (`ISecretStore`, `IObjectStore`, `IEmailSender`…), konkrétny provider sa vyberá podľa prostredia: Azure = Key Vault + Storage Account + Service Bus, Kubernetes = K8s Secrets/Vault + MinIO + NATS, on-prem = súbor + lokálny FS. Aplikačný kód nikdy nevie, kde beží.

Backend-as-a-Service poskytuje vopred pripravené serverové primitívy tak, aby vývojár riešil hlavne frontend a biznis logiku. Litmusový test od tímu Supabase: *„Dokáže užívateľ tento produkt spustiť s ničím iným než PostgreSQL databázou?"* Nasledujúce sekcie rozoberajú každý blok do hĺbky v jednotnej štruktúre: **Čo to je → Musí obsahovať → Štandard → Pozor na → Akceptačné kritériá**.

## §1 Provider model — každý komponent je swappable

Prierezový princíp celého frameworku, preto stojí na začiatku. **Žiadny komponent sa neviaže na konkrétnu infraštruktúru.** Framework definuje pre každú platformovú schopnosť rozhranie (port) a dodáva viacero implementácií (adaptérov). Ktorý adaptér sa použije, rozhoduje konfigurácia prostredia — ten istý aplikačný kód beží na Azure, v Kubernetes aj on-prem bez zmeny. Toto je klasický *ports & adapters (hexagonálna)* architektúra a v .NET je prirodzená cez DI: `services.AddBaaS().UseAzure()` vs `.UseKubernetes()`.

> **Rozhrania (ports), ktoré framework definuje**
- `ISecretStore` — čítanie/rotácia tajomstiev (provider secrets, signing keys).
- `IObjectStore` — binárne úložisko objektov (§2.6 storage).
- `IMessageBus` — doručovanie eventov / outbox transport (§3).
- `IEmailSender` / `ISmsSender` / `IPushSender` — messaging kanály (§2.15).
- `ICacheStore` — distribuovaná cache (§2.11).
- `IChangeFeed` — voliteľný CDC hardening pre out-of-band zmeny (§2.5, §8.2); primárny zdroj realtime eventov je in-process outbox (§3.2).
- `IFunctionRuntime` — kde a ako sa vykoná custom logika: in-process / sidecar worker / microVM sandbox (§2.7).
- `ITelemetrySink` — export traces/metrics/logs (§2.12).
- `IBackupTarget` — cieľ záloh (§7).
- `IIdentityProvider` — federácia externej identity (§2.2 / §6).
- `databázový engine` (EF Core provider) — dev vs produkčné nasadenie (§8).

> **Mapovanie rozhranie → provider podľa prostredia**
| Schopnosť (port) | Azure | Kubernetes / cloud-agnostic | On-prem / dev |
|---|---|---|---|
| `ISecretStore` | Azure Key Vault | K8s Secrets / HashiCorp Vault / External Secrets Operator | šifrovaný súbor / user-secrets / env |
| `IObjectStore` | Azure Blob Storage | MinIO / Ceph RGW / S3-kompatibilné | lokálny filesystem |
| `IMessageBus` | Azure Service Bus | NATS / RabbitMQ / Kafka / Redis Streams | in-process (Channels) / outbox v DB |
| `ICacheStore` | Azure Cache for Redis | Redis / Valkey / Garnet | in-memory (HybridCache L1) |
| `IEmailSender` | Azure Communication Services / ACS Email | SendGrid / Mailgun / SMTP | konzolový sender (dev), MailHog |
| `ISmsSender` | Azure Communication Services | Twilio / Vonage / MSG91 | konzolový sender (dev) |
| `IPushSender` | Azure Notification Hubs | FCM / APNs | no-op (dev) |
| `ITelemetrySink` | Application Insights | OTLP → Grafana/Tempo/Prometheus/Jaeger | konzola / lokálny OTel collector |
| `IBackupTarget` | Blob + Azure Backup (Azure SQL má PITR built-in) | S3 + pgBackRest / Barman (Postgres-specifické) | lokálny disk / NFS / kópia SQLite súboru |
| `IIdentityProvider` | Microsoft Entra ID | Keycloak / Auth0 / generický OIDC | lokálny OpenIddict issuer |
| `IFunctionRuntime` | Azure Container Apps Sandboxes (microVM) | Kata Containers / Firecracker na K8s; sidecar worker | in-process / BackgroundService worker |
| **Databázový engine** | Azure SQL / Azure Database for PostgreSQL | PostgreSQL (self-managed / operator) | **SQLite** — jeden súbor, nulová inštalácia |

> **Pozor na**
- **Least-common-denominator pasca (znova):** rozhranie sa musí navrhnúť na základe *schopností*, nie najslabšieho providera. Providery deklarujú capabilities (`SupportsServerSideEncryption`, `SupportsPresignedUpload`, `SupportsTransactionalOutbox`) a framework sa degraduje kontrolovane — nie potichu vypne feature. Databázový engine je teraz tiež swappable port — podrobné odôvodnenie, prečo to tentokrát *nie je* least-common-denominator kompromis, je v §8.
- **Sémantické rozdiely providerov:** Azure Blob vs S3 majú iné konzistenčné garancie, iné limity na veľkosť, iný signed-URL model. Rozhranie musí definovať kontrakt (vrátane chybových stavov), nie len signatúry metód — inak „funguje na Azure, padá na MinIO".
- **Konfiguračný drift medzi providermi:** každý provider potrebuje inú konfiguráciu; validuj ju pri štarte (fail-fast s jasnou správou „chýba AZURE_KEYVAULT_URI"), nie pri prvom použití za behu.
- **Testovateľnosť:** každý port má in-memory/fake implementáciu pre testy; integračné testy bežia proti reálnym providerom cez Testcontainers (MinIO, Redis, Postgres, NATS).
- **Secrets bootstrap paradox:** `ISecretStore` potrebuje credentials na prístup k secret store — tie musia prísť z prostredia (managed identity na Azure, service account v K8s), nie z ďalšieho secret store. Managed identity / workload identity je preferovaná cesta (žiadne credentials v configu vôbec).

> **Akceptačné kritériá**
- Prepnutie `UseAzure()` → `UseKubernetes()` nevyžaduje zmenu aplikačného ani doménového kódu, len konfiguráciu.
- Ten istý E2E test suite prejde proti všetkým providerom daného portu (contract tests per port).
- Chýbajúca alebo neplatná konfigurácia providera zlyhá pri štarte s actionable správou, nie za behu.
- Nový provider sa dá pridať ako samostatný NuGet balík bez zásahu do jadra (open/closed).

## §2 Anatómia BaaS — komponenty do hĺbky

> **Ako čítať „Musí obsahovať"**
Zoznamy „Musí obsahovať" popisujú **cieľový stav zrelej platformy** — kompletnú doménu, aby architekt videl celé ihrisko. *Nie sú to MVP požiadavky.* Súčet všetkých položiek je niekoľko človeko-rokov práce; tento dokument zámerne definuje *cieľový produktový záber*, nie stratégiu dodávky — fázovanie a scoping v čase sa rieši osobitne (predbežná poznámka v §13.4).

### 2.1 Database layer & data API

**Čo to je a prečo existuje:** jadro BaaS. Vývojár definuje schému a platforma automaticky vystaví CRUD API bez písania kontrolerov. Fakticky je to *kontrakt medzi databázou a klientom* — každé rozhodnutie tu (query language, pagination model, error formát) sa propaguje do všetkých SDK a do všetkého, čo si agenti a vývojári navyknú písať. Zle navrhnutý query language sa už nikdy nedá opraviť bez breaking change.

**Ako to riešia konkurenti:** Supabase generuje REST cez **PostgREST** (URL syntax: `?age=gte.18&order=created_at.desc&select=id,name,posts(title)` — filtre, relation embedding aj projekcie v URL) a GraphQL cez `pg_graphql`. Firestore má obmedzené queries bez joinov (nutná denormalizácia). Convex robí queries ako TypeScript funkcie v databáze (reactive). PocketBase má vlastnú filter syntax nad collections. Appwrite dokumentové API nad MariaDB s vopred definovanými atribútmi.

> **Musí obsahovať**
- **CRUD endpointy per entita** — list, get by PK, create, update (partial/PATCH), delete; **upsert**; **bulk operácie** (batch insert/update/delete, transakčné).
- **Filtrovanie** — ucelená sada operátorov: `eq neq gt gte lt lte like ilike in is-null contains` + logické `and/or/not` so zátvorkovaním. Operátory musia byť *allow-listované* (nie voľný SQL).
- **Sorting** multi-column s direction; **projekcie** (field selection) — šetria payload aj bránia leakom.
- **Pagination v dvoch režimoch:** offset/limit pre jednoduché UI a **keyset/cursor pagination** pre veľké datasety (offset na 1M riadkov degeneruje). Max page size limit vynútený serverom.
- **Relation embedding** (expand FK vzťahov) s limitom hĺbky; **agregácie** aspoň count/min/max/sum — count ako opt-in (viď Pozor).
- **Validácia zo schémy** — NOT NULL, dĺžky, typy, FK existencia; chyby ako **RFC 7807 problem details** so strojovo čitateľným kódom.
- **Computed / počítané polia** — hodnota odvodená z iných polí *toho istého riadku* (napr. `total = unitPrice * amount`). Pre čistú aritmetiku sa mapuje na **DB stored generated column** (`GENERATED ALWAYS AS (…) STORED`, funguje na Postgres/SQL Server/SQLite) — dopočíta databáza, nedá sa obísť ani pri priamom zápise. Zložitejší, ale len app-side variant je CEL výraz v API pipeline.
- **Rollup / agregačné polia** — hodnota agregovaná cez *súvisiace záznamy* (napr. `invoiceTotal = sum(items.total)`). Musí byť **first-class deklaratívny koncept s transakčnou konzistenciou**, nie ručný hook — súčet detí na rodičovi je klasický zdroj race condition (pridám/zmením položku a rollup sa rozíde). Framework ho udržiava buď DB triggerom, alebo in-transaction prepočtom; on-read variant (agregačný dotaz pri čítaní) je alternatíva bez denormalizácie, ale nefiltrovateľná.
- **Optimistic concurrency** — ETag / `updated_at` precondition (If-Match), inak si klienti navzájom prepisujú dáta.
- **Idempotency keys** pre POST (klient pošle `Idempotency-Key` header, retry nevytvorí duplikát) — štandard z platobných API, v BaaS takmer nikto nemá, pritom pre agentov a mobilné retry je kľúčový.
- **Živý OpenAPI dokument** generovaný zo schémy (základ pre SDK codegen, agentov aj dokumentáciu).

> **Rozhodovací rebríček — kam patrí počítaná hodnota**
„Dopočet" nie je jedna vec; podľa zložitosti patrí na inú vrstvu (previazané na extensibility gradient §10):

- **Computed field** — čistá aritmetika/výraz nad poľami toho istého riadku, synchrónne, deterministicky, bez vedľajších efektov (`total = unitPrice * amount`, `fullName = first + ' ' + last`). CEL → generated column.
- **Rollup** — agregácia cez súvisiace záznamy (`sum(items.total)`), transakčne konzistentná. Deklaratívne, ale iný mechanizmus než computed.
- **Podmienená hodnota pri zápise** — „ak `stage == 'won'`, nastav `closed_at = now()`" = **before-hook s `mutate`** (§3.3), podmienka CEL. Nie computed field.
- **Zložitejšia logika** (vetvenie, viac krokov, externé volanie) → **automation akcia** (post-commit) alebo **csx funkcia** (§2.6/§2.7). Computed/rollup tu principiálne nestačí — a nemá.
- **DPH ako hraničný príklad:** vyzerá ako computed (`total * 0.20`), ale sadzba je *kontextová* (krajina, typ plnenia, reverse charge), *časovo platná* (sadzba k dátumu zdaniteľného plnenia, nie dnešná) a zaokrúhľovanie má legislatívne pravidlá. Preto patrí do **hooku/funkcie**, nie do výrazu — a pri reálnej fakturácii je to skôr argument pre integráciu s fakturačným systémom než pre reimplementáciu daňovej logiky v Alvo.

Pravidlo: **deterministická hodnota z jedného riadku → computed; agregácia cez záznamy → rollup; čokoľvek podmienené alebo s vedľajším efektom → hook → akcia → csx.**

> **Čo je štandard**
PostgREST URL syntax je de-facto štandard REST query jazyka v BaaS svete (agenti ju poznajú z trénovacích dát — argument pre kompatibilnú syntax). Count sa štandardne rieši cez `Prefer: count=exact|planned|estimated` header, nie automaticky. GraphQL je druhý pilier pre komplexné čítania (Nhost/Hasura ho má ako primárny). Firestore-štýl obmedzených queries sa dnes už považuje za nevýhodu, nie za feature.

> **Pozor na**
- **SQL injection cez filter hodnoty** — jediná obrana je parametrizácia + operator allow-list; žiadna konkatenácia SQL, nikde, vynútené analyzérom.
- **count(*) na veľkých tabuľkách** — drahá operácia, default vypnutá; ponúknuť estimated count z plannera.
- **N+1 a kombinatorická explózia pri relation embedding** — hĺbka max 1–2, kompiluj do JOINov/lateral queries, nie do sekvenčných dotazov.
- **Breaking changes pri zmene schémy** — premenuješ stĺpec a rozbil si všetkých klientov. Potrebuješ stratégiu: deprecation okno, view-based aliasy, alebo API verzie.
- **Vystavenie interných stĺpcov** — default má byť explicit expose (opt-in per tabuľka aj per stĺpec), nie „všetko čo je v DB".
- **Dlhé URL filtre** — komplexné filtre presiahnú URL limity; treba aj POST-based query endpoint (`/query` s JSON telom).

> **Akceptačné kritériá**
- Adversarial test suite (injection cez každý operátor, malformed hodnoty, unicode) prechádza; fuzzing filtra bez pádu.
- p95 latencia filtrovaného listu nad 100k riadkov (indexovaný stĺpec) < 50 ms lokálne; keyset pagination stabilná nad 1M riadkov.
- Žiadna entita nie je dostupná bez explicitného expose + policy; OpenAPI dokument validuje a je konzistentný so skutočným správaním (contract testy).
- Opakovaný POST s rovnakým Idempotency-Key vráti pôvodný výsledok, nevytvorí duplikát.

#### Dynamické / užívateľom definované entity (schema-at-runtime)

**Čo to je:** osobitný režim data layer, kde koncový užívateľ appky (napr. cez agenta v ERP appke postavenej na frameworku) vytvára *nové typy záznamov za behu* — „chcem evidenciu vozidiel s poľami ŠPZ, VIN, majiteľ" — bez toho, aby niekto spúšťal DDL migráciu. Bežný predpoklad §2.1 (schema registry = introspekcia fyzických tabuliek) tu neplatí: fyzická tabuľka na entitu by pri N klientoch × M evidenciách per klient viedla ku katalógovému bloatu (rast systémových objektov, degradácia `vacuum`/plánovača pri desiatkach až stovkách tisíc relácií) a ku krehkému behovému DDL (locking, riziko zlyhania) na operáciu, ktorú má robiť koncový užívateľ opakovane. Riešenie je rovnaké, aké používa Salesforce (custom objects), Airtable, Notion aj Microsoft Dataverse: **metadata-driven generický store** — fixný, malý počet fyzických tabuliek, schéma je dáta, nie DDL.

> **Musí obsahovať**
- **Fixné systémové tabuľky** nezávislé od počtu vytvorených entít: `entity_definitions` (názov, tenant, vlastník), `field_definitions` (názov poľa, typ, povinnosť, väzby), `entity_records` (`tenant_id`, `entity_definition_id`, `data JSONB`, timestampy).
- **Druhý „driver" schema registry** (§2.1) — namiesto introspekcie `information_schema` číta `entity_definitions`/`field_definitions` a vyrába ten istý abstraktný `TableMeta`/`ColumnMeta` model. Data API, rule engine (§2.4), realtime (§2.5) aj automation (§3) tak fungujú *identicky* nad virtuálnou aj reálnou entitou — nepotrebujú (a nesmú) vedieť rozdiel.
- **Typová validácia na aplikačnej úrovni** — keďže DB constraint tu chýba, vynucovanie typu/povinnosti/enum hodnôt musí robiť API vrstva podľa `field_definitions` pred zápisom do `data`.
- **Selektívne indexovanie horúcich polí** — generated column nad `data->>'pole'` (Postgres: `GENERATED ALWAYS AS (data->>'stk_do') STORED`) + bežný B-tree index navrchu, buď explicitne („pin toto pole"), alebo automaticky podľa pozorovaných query patternov.
- **Referenčná integrita medzi virtuálnymi entitami na aplikačnej úrovni** — FK medzi dvoma dynamickými entitami nevynucuje DB, musí to overiť rule engine/API pri zápise (existencia cieľového záznamu, cascade pravidlá).
- **Cesta k materializácii** — keď entita narastie do objemu/zložitosti, kde JSONB bolí (státisíce záznamov, potreba natívnych FK, ťažké agregácie), riadená background operácia prekopíruje dáta do reálnej tabuľky a prepne driver z „virtual" na „physical" pre danú entitu — rovnaké API navonok, iný storage engine naspodku (ten istý princíp „povýšenia" ako SQLite dev → Postgres produkcia z §8.2, len na úrovni jednej entity, nie celého projektu).

> **Pozor na**
- **Agregácie a joiny cez JSONB sú drahšie** než natívne stĺpce — plánovač nemá štatistiky nad extrahovanými hodnotami tak kvalitné ako nad reálnym stĺpcom; komplexné reporty nad veľkým objemom dynamických záznamov sú kandidát na materializáciu.
- **Rast `entity_records` tabuľky** — všetky dynamické entity všetkých tenantov zdieľajú jednu (partitionovanú) tabuľku; nutné partitionovanie podľa `tenant_id` alebo `entity_definition_id`, inak z problému „veľa malých tabuliek" vznikne problém „jedna obrovská tabuľka".
- **Zmena definície poľa nad existujúcimi dátami** — pridanie povinného poľa alebo zmena typu sa netýka len budúcich zápisov; potrebná stratégia pre existujúce záznamy (default hodnota, backfill job, alebo verzovanie definície).
- **Typovaný C# codegen (§9.2) sa na runtime entity nevzťahuje** — compile-time záruky platia len pre dizajnové (fyzické) entity; dynamické entity sa konzumujú cez generické/slabo typované API (dictionary/JSON prístup). Toto napätie treba priznať v dokumentácii aj v SDK dizajne (dva režimy práce s dátami), nie ho maskovať.
- **Limit nie je na počet entít, ale na objem a vzorec prístupu** — komunikuj to používateľovi frameworku správne: virtuálne entity škálujú na tisíce definícií bez katalógového bloatu, ale nie sú zadarmo pri vysokom objeme záznamov a komplexných dotazoch.

> **Akceptačné kritériá**
- Vytvorenie novej dynamickej entity je čistý INSERT do metadát (žiadne DDL, žiadny lock na existujúcich tabuľkách) a je okamžite dostupné cez Data API aj rule engine.
- Rovnaký adversarial a policy test suite (§2.4) prechádza identicky nad fyzickou aj virtuálnou entitou.
- Vytvorenie 10 000 dynamických entít naprieč tenantmi nezvýši počet fyzických databázových objektov nad konštantu.
- Materializácia entity na reálnu tabuľku prebehne bez straty dát a bez zmeny verejného API kontraktu.

### 2.2 Authentication

**Čo to je:** identita užívateľa — kto je na druhej strane requestu. V BaaS je auth komoditizovaná, ale je to zároveň komponent, kde chyba znamená bezpečnostný incident, a kde je najviac „drobných featúr", ktoré rozhodujú o použiteľnosti (magic links, account linking, anonymous upgrade…). Supabase to rieši službou GoTrue (Go), Firebase má najzrelší auth na trhu, PocketBase má auth zabudovaný v collections.

> **Musí obsahovať**
- **Email/heslo** s moderným hashovaním (**Argon2id**, fallback bcrypt), email verification flow, password reset (časovo obmedzené tokeny, jednorazové), password policy konfigurácia.
- **Magic links / OTP cez email** — dnes očakávaný baseline pre consumer appky.
- **OAuth social login** — minimálne Google, Microsoft, GitHub, Apple; plus generický OIDC provider (enterprise: Entra ID, Keycloak).
- **Passkeys / WebAuthn** — v 2025 prešli do GA na Supabase aj Firebase; nový framework bez nich vyzerá zastaralo.
- **MFA** — TOTP minimálne; recovery kódy.
- **Anonymous auth s upgradom** — anonymný user začne používať appku, neskôr sa registruje a *jeho dáta sa musia preniesť* (account linking). Firebase to má vyriešené najlepšie; často podceňované, pritom kritické pre onboarding conversion.
- **Session model:** krátkodobý JWT access token + **rotating refresh token s reuse detection** (použitie starého refresh tokenu = kompromitácia → invaliduj celú token family). JWKS endpoint pre verifikáciu tretími stranami.
- **Account linking / identity merging** — jeden človek, viac providerov (Google aj email/heslo s tou istou adresou) = jeden účet, s explicitnou politikou riešenia kolízií.
- **Service keys / API keys** s scopes — server-side prístup obchádzajúci user policies (ekvivalent Supabase `service_role`), jasne oddelený od klientskych kľúčov.
- **Brute-force ochrana** — rate limiting na auth endpointoch, exponenciálny lockout, CAPTCHA hook.
- **Audit auth eventov** — login, failed login, password change, token refresh — ako súčasť event systému (§3), nie len log.
- **Admin operácie** — user CRUD, ban, force logout, impersonation (s auditom!).

> **Pluggable identity providers — default, ale nahraditeľné**
Framework prichádza s **rozumnými defaultmi** (email/heslo + Google + Microsoft OAuth fungujú out-of-the-box bez konfigurácie navyše), ale autentifikácia je za rozhraním `IIdentityProvider` (§1) presne ako storage či messaging. Dôvody, prečo to nesmie byť zabudované natvrdo: enterprise zákazník potrebuje pripojiť **vlastný Entra ID tenant / Keycloak / Auth0** namiesto vstavaného OpenIddict issuera; iný zákazník chce **vlastný custom auth** úplne (napojenie na existujúci legacy identity systém firmy) — framework mu musí dovoliť *nahradiť* celú auth vrstvu, nie ju len rozšíriť. Prakticky: `IIdentityProvider` definuje kontrakt (overiť credentials → vydať claims), built-in implementácie (Local, Google, Microsoft, GitHub, Apple, generický OIDC) sú len jedny z mnohých; užívateľ frameworku môže dodať vlastnú (`services.AddBaaSAuth().AddProvider<MyCorpSsoProvider>()`) bez zásahu do jadra. Claims z ľubovoľného providera sa mapujú do jednotného interného modelu identity, aby RBAC (§2.3) a rule engine (§2.4) pracovali nad providermi konzistentne. Základ vstavaného `local` providera stojí na **ASP.NET Core Identity (.NET 10)** — moderné token endpointy (`MapIdentityApi`, bearer tokens), hashovanie, lockout, 2FA a e-mailové flows sú udržiavané Microsoftom; Alvo na Identity *stavia*, nepíše vlastnú user/password mechaniku vedľa nej.

> **Čo je štandard**
OAuth 2.1 / OIDC; JWT s asymetrickým podpisom (RS256/ES256) a key rotation cez JWKS; refresh token rotation with reuse detection (Auth0/Supabase model); passkeys ako rastúci štandard. E-maily (verification, reset) cez pluggable provider — framework nesmie byť viazaný na konkrétny SMTP/SaaS.

> **Pozor na**
- **Uloženie tokenov na klientovi** — SDK musí mať pluggable secure storage a dokumentované odporúčania (httpOnly cookies pre web, secure storage pre mobil); XSS + localStorage je klasická diera.
- **Invalidácia sessions po zmene hesla** — JWT je stateless; treba token family revocation alebo krátke expirácie + revocation list.
- **Kolízie pri account linkingu** — dva providery, rovnaký email, rôzni ľudia (email recyklácia). Nikdy nespájaj automaticky bez verifikácie.
- **Anonymous → registered upgrade** — ak sa neprenesú dáta (FK na user id), užívateľ „stratí" všetko; toto musí byť transakčná operácia frameworku, nie úloha pre appku.
- **Clock skew** pri JWT validácii; **veľkosť claims** (custom claims v tokene rastú → headers limity).
- **Email deliverability** — bez správneho SPF/DKIM nastavenia sú verification maily v spame; dokumentuj a poskytni dev-mode konzolový sender.

> **Akceptačné kritériá**
- E2E testy všetkých flows: register → verify → login → refresh → password change → staré sessions mŕtve → logout.
- Refresh token reuse test: použitie rotovaného tokenu invaliduje celú family do 1 s.
- Token validácia pridáva < 1 ms overhead (cached JWKS); anonymous upgrade prenesie 100 % dát v transakcii.
- Secrets (heslá, tokeny) sa nikdy neobjavia v logoch — automatizovaný scan logov v CI.

### 2.3 Identita & role-based access control (RBAC) — správa užívateľov a oprávnení

**Čo to je:** vrstva medzi „kto si" (§2.2 Authentication) a „čo smieš urobiť s týmto konkrétnym riadkom" (§2.4 Authorization/RLS) — administratívny model rolí, tímov a oprávnení, cez ktorý prevádzkovateľ appky spravuje *ľudí*, nie dáta. Je to samostatný komponent, ktorý sa v prehľadoch BaaS ľahko stráca v tieni row-level policies, ale bez neho nemá admin ako priradiť „Janovi manažérsku rolu" alebo „vytvoriť tím Marketing s troma ľuďmi" — musel by písať SQL/rules ručne pri každej zmene organizačnej štruktúry.

> **Musí obsahovať**
- **Built-in roly** ako baseline: `anon`, `authenticated`, `admin` — fungujú bez konfigurácie od prvého dňa.
- **Custom roly** definovateľné prevádzkovateľom appky (nie len frameworkom) — `editor`, `viewer`, `billing-manager` — s vlastnou sadou oprávnení.
- **Permission model:** jemnozrnné oprávnenia (per entita × operácia, per custom endpoint, per admin akcia), skladané do rolí. Rola je pomenovaná množina oprávnení, nie kód naprogramovaný v appke.
- **Teams / groups / organizácie** — používatelia zoskupení do tímov s vlastným rolovým priradením (Appwrite `Role.team("writers")` je referenčný vzor); hierarchia tímov pre väčšie organizácie.
- **Priraďovanie a správa** — CRUD nad rolami a ich priradeniami cez admin API/UI aj programovo (`await users.AssignRole(userId, "editor")`); bulk operácie (priraď rolu skupine používateľov naraz).
- **Roly ako vstup pre rule engine (§2.4)** — `@user.role` a `@user.teams` dostupné vo výrazoch autorizačných pravidiel aj automation rules (§3); RBAC teda *napája* row-level politiky, nie je s nimi v konflikte.
- **Custom claims/atribúty na užívateľovi** nad rámec rolí (napr. `department`, `region`) — používané v pravidlách pre jemnejšiu granularitu než čisté role.
- **Delegovaná administrácia** — tenant/team admin smie spravovať role *len vo svojom* tenante/tíme, nie globálne (napojenie na §4 multi-tenancy).
- **Role hierarchy / dedičnosť** (voliteľné, ale bežné) — `admin` automaticky dedí oprávnenia `editor`; explicitne definovaný strom, nie implicitná mágia.

> **Čo je štandard**
RBAC (role → permissions → users) je priemyselný štandard; ABAC (attribute-based, cez custom claims) je bežný doplnok pre prípady, ktoré čisté role nepokryjú. Appwrite Teams/Roles a Postgres roly + `GRANT` sú referenčné vzory. Pre .NET je toto prirodzene **ASP.NET Core Identity Roles + Claims** ako základ, rozšírený o custom permission store.

> **Pozor na**
- **Zámena RBAC za autorizáciu dát** — RBAC hovorí „Ján je editor", rule engine (§2.4) hovorí „editor smie meniť len faktúry svojho oddelenia". Bez druhej vrstvy je RBAC hrubozrnný (buď vidí všetko, alebo nič); framework musí jasne komunikovať, že ide o dve doplnkové vrstvy, nie alternatívy.
- **Privilege escalation cez self-service priradenie** — užívateľ nesmie vedieť priradiť sám sebe vyššiu rolu; zmena vlastnej role musí ísť cez niekoho s vyšším oprávnením, s auditom (§5).
- **Role explosion** — desiatky mikro-rolí namiesto pár rolí + atribútov vedie k neudržateľnej matici; ABAC (custom claims v pravidlách) je často lepšia cesta než ďalšia rola.
- **Cache rolí v JWT** — ak sú role/permissions v tokene, zmena role sa neprejaví, kým token nevyprší alebo nie je revokovaný (napojenie na §2.2 token invalidation).
- **Cross-tenant leak rolí** — rola priradená v tenante A nesmie platiť v tenante B (§4).

> **Akceptačné kritériá**
- Priradenie/odobratie role sa prejaví v autorizačnom rozhodnutí do definovaného SLA (buď okamžite pri policy-time lookupe, alebo do vypršania krátkeho tokenu — zdokumentované a testované).
- Užívateľ bez oprávnenia „spravuj role" nedokáže zmeniť vlastnú ani cudziu rolu (adversarial test).
- Delegovaný tenant/team admin nevie priradiť rolu mimo svojho rozsahu.
- Zmena role je auditovaná (§5) s pred/po hodnotou a actorom.

> **.NET stavebné bloky**
**ASP.NET Core Identity** (Roles + Claims, built-in) ako základ; rozšírenie o vlastný permission store (tabuľka rolí ↔ oprávnení, nie len pevný enum) a **Finbuckle.MultiTenant** pre tenant-scoped role (§4). Autorizačné rozhodnutia cez `IAuthorizationHandler`/policies, ktoré delegujú na ten istý expression engine ako rule engine (§2.4/§3) — jeden zdroj pravdy pre „čo znamená byť editor".

### 2.4 Authorization (row-level rules) — skutočná architektonická voľba

**Čo to je:** kto smie čo s *ktorými konkrétnymi riadkami* dát — jemnozrnná vrstva nad hrubozrnným RBAC (§2.3). Kým RBAC odpovie „Ján je editor", táto vrstva odpovie „editor smie meniť faktúru, len ak patrí jeho oddeleniu". Toto je najdôležitejšie architektonické rozhodnutie celého BaaS, pretože auto-generované API vystavuje databázu priamo klientom — autorizačná vrstva je jediné, čo stojí medzi anonymom a dátami. Dva tábory: *resource-level policy* (Postgres RLS, Firestore Security Rules — pravidlo „býva" pri dátach) a *operation-level policy* (check v každom endpointe — Firebase Data Connect ju zvolil vedome, lebo *„open insecure rules sú obrovský problém"*).

| Platforma | Model | Vynucovanie | Syntax |
|---|---|---|---|
| **Supabase** | Postgres RLS | Database-side | `CREATE POLICY "own" ON profiles FOR SELECT TO authenticated USING (auth.uid() = user_id)`; `USING` filtruje čítania, `WITH CHECK` validuje zápisy. Platí pre REST, realtime aj priame SQL. |
| **Firebase** | Security Rules (DSL) | Service-side | `allow read: if resource.data.authorId == request.auth.uid`. Nový jazyk, emulator na testovanie, čítania v pravidlách sa účtujú. |
| **Appwrite** | Permissions (role-based) | Application-side | `Permission.read(Role.any())`, `Permission.update(Role.team("writers"))` na úrovni kolekcie aj dokumentu. |
| **PocketBase** | API Rules (výrazy) | Application-side | `@request.auth.id != "" && user = @request.auth.id` per collection a operáciu. |

> **Musí obsahovať**
- **Per-operation pravidlá** — samostatne pre list, get, create, update, delete (PocketBase model); default = deny.
- **Row-level** podmienky nad stĺpcami riadku + kontextom užívateľa (`@user.id`, `@user.role`, custom claims) — a pri update prístup k *starej aj novej* hodnote.
- **Field-level control** — hidden columns (nikdy sa neserializujú: `password_hash`, interné poznámky) a read-only columns (server odmietne v payload).
- **Policy-as-code** — pravidlá žijú v repe (migrations/config), UI editor je len pohodlný zápis toho istého.
- **Policy simulator / dry-run** — „môže user X urobiť Y na riadku Z?" ako API aj UI nástroj; bez toho sa pravidlá ladia produkčnými incidentmi. (Firebase má emulator, Supabase „run as role" v SQL editore.)
- **Jednotné vynucovanie** — tá istá politika platí pre REST, GraphQL, realtime push, storage aj automation (§3). Jedna definícia, všetky kanály.
- **Service-role bypass** s auditom každého použitia.

> **Čo je štandard**
Postgres RLS je zlatý štandard database-side vynucovania (matematicky nepriestrelné pre všetky prístupové cesty). PocketBase rules sú zlatý štandard jednoduchosti. Kompromis pre .NET: application-side expression rules, ktoré sa *kompilujú do SQL predikátov* (nikdy post-filter v pamäti) — čitateľnosť PocketBase + vynucovanie v databáze; voliteľne delegácia na natívne RLS (ako to robí Data API builder cez session context z JWT).

> **Pozor na**
- **RLS-off footgun (Supabase issue #11538):** tabuľka bez RLS = plný read-write pre anon key; views s `security_invoker = false` vystavené v API. Nový framework: *nič nie je exposed, kým nemá politiku*.
- **Výkon pravidiel** — subquery v policy sa vyhodnocuje per riadok; ťažké podmienky (membership check) treba cacheovať do claims alebo materializovať.
- **Information leak cez agregácie** — count/exists nad riadkami, ktoré užívateľ nevidí, prezrádza existenciu dát; agregácie musia bežať nad policy-filtrovanou množinou.
- **Autorizácia realtime pushu** — najťažšie miesto (viď §2.5 nižšie); nesmie byť dodatočná záplata.
- **Vlastný DSL** — Firebase Security Rules learning curve je dokumentovaná bariéra. Použi jazyk, ktorý cieľovka pozná (SQL výrazy / C# expressions), a fuzz-testuj parser.

> **Akceptačné kritériá**
- Two-user adversarial suite: user A nikdy nevidí/nezmení dáta usera B — cez REST, GraphQL, realtime, storage aj automation payloady.
- Pravidlo odkazujúce na neexistujúci stĺpec zlyhá pri uložení (compile-time), nie pri requeste.
- Property-based testy dokazujú, že preklad pravidla → SQL nikdy neinterpoluje užívateľský vstup.
- Policy simulator odpovie na ľubovoľnú kombináciu (user, operácia, riadok) identicky ako produkčné vynucovanie.

### 2.5 Realtime

**Čo to je:** push zmien dát klientom bez pollingu. Tri techniky change data capture: **logical replication / WAL** (Supabase Realtime číta write-ahead log — kompletné, trigger-less), **LISTEN/NOTIFY** (jednoduchšie, s limitmi) a **polling** (fallback). Firebase má realtime natívne od počiatku; Convex ide ďalej — reactive queries, kde sa pri zmene dát automaticky invalidujú a re-pushujú výsledky dotknutých dotazov.

> **Musí obsahovať**
- **Typy subscriptions:** zmeny entity (INSERT/UPDATE/DELETE), *filtrovaná* subscription (len riadky spĺňajúce podmienku), single-record subscription, **Presence** (kto je online, zdieľaný stav) a **Broadcast** (ephemeral správy klient↔klient bez zápisu do DB) — trojica, ktorú etabloval Supabase.
- **Payload dizajn:** event nesie `type`, entitu, `record` a pri UPDATE aj `old_record` (Supabase webhook/realtime kontrakt) — bez old hodnoty sa nedajú robiť diffy ani podmienky „zmenil sa stĺpec X".
- **Autorizácia na push:** pred doručením eventu konexii sa vyhodnotí read policy daného užívateľa na daný riadok. Batchuj (per tick zoskup eventy, jeden authz dotaz), inak je to O(events × connections).
- **Reconnect sémantika:** klient sa po výpadku pripojí s resume tokenom / posledným offsetom; framework buď dodrží continuity (replication slot drží pozíciu), alebo explicitne signalizuje „refetch needed". Nedefinovaná sémantika = klienti s tichými dierami v dátach.
- **Backpressure a quoty** — limit správ/s na konexiu, limit konexií na užívateľa; pomalý klient nesmie zablokovať dispatcher.
- **Scale-out plán** — single node v MVP, dokumentovaný backplane (Redis / Orleans) pre horizontálne škálovanie.

> **Čo je štandard**
WebSockets ako transport (SSE fallback). **Primárny zdroj eventov v tomto frameworku je in-process emisia z unified event systému (§3.2 outbox)** — engine-agnostická, funguje na SQLite, Postgrese aj Azure SQL, s nižšou latenciou než externé čítanie logu. WAL-based CDC (Supabase model) je *voliteľný hardening na Postgrese* pre zachytenie out-of-band zmien (priame SQL mimo frameworku — viď §8.2 a §10.3); ekvivalent na SQL Serveri je Change Tracking. Delivery sémantika *at-most-once s možnosťou refetchu* je bežná — nikto z BaaS negarantuje at-least-once do klienta (to je úloha §3 automation, nie realtime UI vrstvy). Presence + Broadcast sú dnes očakávaná súčasť, nie bonus.

> **Pozor na**
- **(Postgres hardening) Replication slot bloat:** ak konzument WAL zaostáva alebo umrie, Postgres drží WAL segmenty pre slot → *disk sa zaplní a spadne celá databáza*. Monitoring lagu slotu + automatický safety drop s alertom je povinný.
- **LISTEN/NOTIFY limity:** payload max 8 kB, notifikácie sa strácajú pri odpojení, nemajú persistenciu — použiteľné len ako signál „niečo sa zmenilo, refetchni", nie ako spoľahlivý event stream.
- **Thundering herd** pri reconnecte po výpadku — jitter + staggered resubscribe.
- **Filtrované subscriptions a UPDATE cez hranicu filtra:** riadok predtým spĺňal filter a po update už nie (alebo naopak) — klient musí dostať synthetic DELETE/INSERT, inak má nekonzistentný lokálny stav. Klasická chyba naivných implementácií.
- **PII v payloadoch** — možnosť thin payload (len PK + typ), klient si dofetchne cez policy-checked API.

> **Akceptačné kritériá**
- INSERT → event u klienta < 250 ms lokálne; 2k súbežných konexií na 4 vCPU node.
- Two-client authz test: subscriber nikdy nedostane event pre riadok, ktorý mu policy zakazuje — vrátane UPDATE, kde stará hodnota bola viditeľná a nová nie.
- (Postgres hardening) Kill konzumenta WAL na 10 min → po reštarte pokračuje bez straty; slot lag metrika exportovaná, alert nad prahom.
- Filter-boundary test (update presunie riadok cez hranicu filtra) generuje korektné synthetic eventy.

> **.NET stavebné bloky**
**SignalR** (transport, groups, presence) nad **in-process eventmi z outboxu (§3.2)** — primárna, engine-agnostická cesta. Pre voliteľný Postgres hardening (out-of-band zmeny): **Npgsql logical replication** — pgoutput stream ako `IAsyncEnumerable`, replication slot drží pozíciu; Supabase-štýl WAL realtime sa dá postaviť **natívne v C#** bez externej Elixir služby. Na SQL Serveri: Change Tracking polling za rozhraním `IChangeFeed`. Pre scale-out: Redis backplane pre SignalR alebo Orleans.

### 2.6 Storage

**Čo to je:** upload, uloženie a serving súborov s rovnakým autorizačným modelom ako dáta. Supabase Storage ukladá metadáta do Postgres (takže platia RLS politiky), objekty do S3-kompatibilného backendu, podporuje TUS resumable uploads, on-the-fly image transformations a Smart CDN.

> **Musí obsahovať**
- **Buckets** s per-bucket politikami (read/write/delete — ten istý rule engine ako dáta), max file size, MIME allow-list.
- **Metadáta v databáze** (`objects` tabuľka: bucket, path, size, MIME, owner, timestamps, custom metadata) — aby pravidlá mohli referencovať `owner_id` a aby súbory boli súčasťou event systému (§3: `storage.object.created`).
- **Signed URLs (Valet Key pattern)** — časovo obmedzené na download aj upload; **presigned direct-to-S3 upload**, aby veľké súbory netiekli cez app server (bandwidth + pamäť). Ide o oficiálny *Valet Key* cloud design pattern (Azure Architecture Center): klient dostane dočasný, na jeden objekt a jednu operáciu obmedzený kľúč namiesto plných credentials k úložisku — analógia parkovacieho kľúča. Nezamieňať s Key Vault (§7.1), čo je secret store provider, nie prístupový vzor.
- **Resumable uploads (TUS protokol)** — pre mobil a veľké súbory nevyhnutné; multipart pre S3.
- **Range requests** (video/audio streaming), správne cache headers, ETag.
- **Lifecycle** — TTL/expiry pravidlá, orphan reconciliation job (metadáta bez objektu a naopak).
- **Image transforms** (resize/crop/format) — môže byť fáza 2, ale API dizajn (transform parametre v URL + cache) treba navrhnúť od začiatku.
- **Hook pre AV scan / content validation** pred sprístupnením (event `object.created` → funkcia → schválenie).

> **Čo je štandard**
S3 API ako lingua franca úložísk (MinIO pre self-host, AWS/Azure/R2 pre cloud); TUS 1.0 pre resumable; signed URLs s HMAC. Metadáta-v-DB model (Supabase) je architektonicky nadradený „storage ako čierna skrinka", lebo zjednocuje policies a eventy.

> **Pozor na**
- **Content-type spoofing** — nikdy never klientom deklarovanému MIME; sniffuj magic bytes, servuj s `X-Content-Type-Options: nosniff`; HTML/SVG upload = stored XSS vektor (servuj z izolovanej domény alebo s `Content-Disposition: attachment`).
- **Path traversal** v path parametroch; normalizácia unicode názvov.
- **Konzistencia metadát vs objektov** — dual-write problém (DB + S3); rieš two-phase (najprv DB záznam pending, potom objekt, potom commit) + reconciliation.
- **Verejné buckety omylom** — public musí byť explicitný, krikľavo označený stav.
- **EXIF/GPS metadáta** v obrázkoch — privacy; ponúkni stripping.
- **ImageSharp licencia** — Split License, build-time license key od v4; použi SkiaSharp/Magick.NET (viď §9).

> **Akceptačné kritériá**
- TUS upload prerušený na 50 % pokračuje po reconnecte a dokončí sa s korektným checksumom.
- Policy testy: signed URL po expirácii vráti 403; user A nestiahne objekt usera B žiadnou cestou.
- 1 GB upload nezvýši pamäť app servera o viac než konštantu (streaming, žiadne buffrovanie celého súboru).
- Orphan reconciliation nájde a vyčistí umelo vytvorené nekonzistencie v teste.

### 2.7 Serverless functions / custom logic

**Čo to je:** miesto pre kód, ktorý sa nezmestí do deklaratívneho CRUD — validácie, integrácie, výpočty. Modely konkurencie: Supabase Edge Functions (Deno/TS, globálne), Firebase Cloud Functions (Node/Python/Go, najzrelšie event triggery), Appwrite Functions (15+ runtimes, izolované kontajnery, **sync execution s 30 s hard timeoutom**, async cez queue), Convex (TS mutations/queries/actions s durable execution), PocketBase (Go extending alebo JSVM hooks — synchronný goja, bez async/await).

> **Musí obsahovať**
- **Štyri spúšťače:** HTTP endpoint (vlastná URL — zároveň slúži ako inbound webhook receiver pre Stripe a pod.), event trigger (naviazaný na event katalóg §3), schedule (cron + one-shot delayed execution), manuálne spustenie (z admin UI / SDK).
- **Sync vs async execution** s jasnou sémantikou: sync = krátky timeout (Appwrite 30 s je rozumný benchmark), async = queue + retries + dead-letter.
- **Kontext funkcie:** identita volajúceho, prístup k dátam *s* policy vynucovaním (default) aj privilegovaný režim (explicitný, auditovaný), secrets/env management.
- **Logy a korelácia** — request-id tečie z API cez event do funkcie; logy per execution v admin UI.
- **Versioning a rollback** nasadených funkcií; lokálny beh identický s produkčným (dev/prod parity).
- **Ochrana proti rekurzii** — funkcia, ktorá triggerne event, ktorý spustí ju samú (Appwrite to rieši blokovaním function-on-function eventov; viď §3 loop protection).
- **Concurrency limity** per funkcia — ochrana DB pred vlastným kódom užívateľa.

> **Čo je štandard**
Event-driven functions (na DB zmenu, auth event, storage event) sú definičná vlastnosť kategórie — Firebase ich etabloval. Git-based deploy (Appwrite) je očakávaný DX. Pre .NET je unikátna možnosť troch úrovní v jednom runtime: *(1) embedded C# endpointy v hoste, (2) hooks/interceptory, (3) izolované pluginy (AssemblyLoadContext) či Roslyn skripty* — bez jazykového switchu, ktorý má PocketBase (Go↔JS) aj Supabase (SQL↔Deno).

> **Rozhodnuté: custom logika v standalone režime = C# skripty (.csx)**
Standalone režim (§2.14) dostáva custom kód cez **Roslyn `.csx` skripty** — jediná cesta verná agentickému zámeru (§0): agent skript vygeneruje, dostane späť plnú Roslyn diagnostiku (`CS1002 ; expected, line 12` — presne štruktúrovaná chyba z §9), opraví sa a beží. Žiadny build pipeline mimo systému.

- **Mechanika:** skripty žijú v mounte / v project descriptor bundli; compile-on-load s cache podľa content hashu; beh v AssemblyLoadContext; kompilačná chyba zhodí *aktiváciu* skriptu (s diagnostikou), nie request v produkcii.
- **Trust model natvrdo:** skript = *admin-level kód, plná dôvera* — identicky ako plugin DLL (kto vie mountnúť súbor, aj tak vlastní kontajner). ALC ani Roslyn analyzer nie sú bezpečnostná hranica; analyzer (zákaz `System.IO`, raw `SqlConnection`…) je defense-in-depth zábradlie, nie väzenie.
- **Editácia z admin UI je opt-in** (`ALVO_SCRIPTS_ALLOW_UI_EDIT`, default vypnuté), za samostatným permission, každá zmena auditovaná (§5), aktivácia až po úspešnej kompilácii + dry-run — kompromitovaný admin účet nesmie znamenať RCE formulárom „zadarmo".
- **Pipeline pravidlá platia rovnako:** before-hooky zo skriptov majú ten istý časový rozpočet a zákaz siete ako C# hooky v hoste; ťažká práca patrí do after-side. Skript dostáva `AlvoScriptContext` (dáta cez `IAlvoData` s policy, eventy, logger, limitovaný HTTP klient).
- **Hranica zostáva:** csx nikdy nebude cesta pre *nedôveryhodný* kód — pre prípadný hostovaný multi-tenant SaaS pôjde cudzí kód out-of-process (samostatný worker kontajner, YARP proxy); WASM sandbox je kandidát na budúcu náhradu, dnes je .NET→WASI predčasné.

> **Exekučný model — dve nezávislé osi (`IFunctionRuntime` port)**
„Kde a ako sa custom logika vykoná" nie je jedno rozhodnutie, ale **dve nezávislé osi**, ktoré sa dlho miešali dokopy:

- **Os 1 — kde beží (izolácia):** *in-process* (ALC, žiadna izolácia, len trusted) → *sidecar worker kontajner* (izolácia stability — worker spadne, host beží ďalej) → *microVM sandbox* (hardvérová izolácia pre netrusted, Azure Container Apps Sandboxes / Kata / Firecracker).
- **Os 2 — synchrónne vs cez frontu:** *sync* (v request ceste, krátky timeout) vs *queued* (orchestrátor zapíše event do outboxu §3.2, worker si ho vyzdvihne cez `IMessageBus` a spustí csx mimo hlavnej appky — neblokuje API, retry, škálovanie).

Kľúč: **odsun do workera (os 2) a typ izolácie (os 1) sú nezávislé.** Worker-based orchestrácia dáva zmysel v *každom* režime (nezablokuje API, umožní retry); microVM je nutné len pri netrusted kóde. Preto je exekučný backend **swappable port s rozumnými defaultmi**, konfigurovateľný:

| Režim | Orchestrácia | Executor (default) | Izolácia |
|---|---|---|---|
| Dev / embedded (trusted) | in-process pipeline | in-process, prípadne `BackgroundService` worker | žiadna (trust: admin kód) |
| Self-host (trusted) | outbox + `IMessageBus` | sidecar worker kontajner (compose/Aspire) | stability (proces), nie security |
| Hostovaný SaaS (untrusted) | Durable Functions / control plane | microVM sandbox worker | hardvérová, per-tenant |

Ten istý csx kód a tá istá orchestrácia (outbox + bus) naprieč režimami — mení sa len executor a hranica. Odporúčanie: **nestavať vlastný sandbox** (mesiace práce), použiť hotové (Container Apps Sandboxes na Azure, Kata RuntimeClass na K8s) ako `IFunctionRuntime` provider.

> **Pozor na**
- **Cold starts** — dokumentovaný pain point Firebase; in-process .NET model ich eliminuje úplne (ďalší argument pre embedded prístup).
- **Izolácia user kódu** — csx beží in-process s plnou dôverou (viď rozhodnutie vyššie): pre *vlastný* self-host OK, pre untrusted kód (hostovaný SaaS) je jediná bezpečná cesta out-of-process runner; ALC + resource limity chránia stabilitu, nie bezpečnosť.
- **Secrets v logoch a env**; **timeout a cancellation** — funkcia musí dostať CancellationToken a framework ho musí vynútiť.
- **Synchronné hooks blokujú transakciu** — before-hooks musia mať rozpočet (ms) a nesmú volať sieť (viď §3 blocking vs non-blocking).

> **Akceptačné kritériá**
- Event → async funkcia s garanciou at-least-once (crash test: kill worker uprostred, funkcia sa vykoná po reštarte).
- Nekonečná rekurzia (funkcia triggeruje samu seba) je zastavená limitom hĺbky a alertovaná, nie DoS.
- Funkcia presahujúca timeout je zabitá, transakcie čisto rollbacknuté, execution označená failed s dôvodom.

### 2.8 Admin dashboard / studio

**Čo to je:** vizuálne rozhranie nad všetkým vyššie. Supabase Studio: table editor, SQL editor, RLS policy editor, logy, AI assistant. PocketBase: kompletný admin v jednom binári. Directus: Data Studio vrátane vizuálneho automation buildera (Flows). Pre Alvo je dashboard **prvý dojem produktu** — pri „describe the intent" pitchi musí sám vyzerať moderne a hotovo, nie ako vygenerované CRUD lešenie.

> **Musí obsahovať — funkčne**
- Schema editor (tabuľky, stĺpce, vzťahy, dynamické entity) — **ktorý generuje migrácie / descriptor**, nie priame ALTER (inak drift medzi UI a repo).
- Data browser s filtrovaním, inline editáciou, bulk akciami a **auditom admin zásahov**.
- Policy editor s live validáciou + **policy simulator** (§2.4); rules/automation builder (§3); csx editor s diagnostikou (§2.7, UI-edit gate).
- User & RBAC management, storage browser, logs/executions explorer, webhook delivery log + redelivery, API docs (živé OpenAPI cez Scalar).
- Správa projektov (standalone), export/import descriptora, admin portál RBAC (viewer/developer/admin), first-run wizard.

> **Musí obsahovať — technológia & dizajn**
- **Blazor** (jeden jazyk s jadrom — C#). Primárne *Blazor Web App (.NET 10)* so server interactivitou pre admin (rýchly štart, žiadny cold WASM download); WASM režim ako voľba pre plne klientske/offline scenáre. Rozhodnutie render módu je implementačný detail, ale default = server interactive pre nižšiu latenciu a menší bundle.
- **Vlastný moderný dizajn, NIE default Blabor/Bootstrap look.** Cieľ: čistý, súčasný vzhľad (Supabase Studio / Linear / Vercel liga), nie „vygenerovaná administrácia". Vlastný design systém (tokeny: farby, spacing, radius, typografia; dark/light mód), nie neprispôsobená knižnica.
- **Bohaté komponenty** — zrelá Blazor komponentová knižnica ako základ (MudBlazor / Radzen / FluentUI-Blazor), ale *pretémovaná* na vlastný design systém, nie použitá s default témou. Data grid s virtualizáciou, command palette (⌘K), toasty, drawer/side panely, skeleton loading, prázdne stavy.
- **Plne responzívne, mobile-first.** Admin použiteľný z telefónu (rýchla kontrola dát, schválenie, log). Adaptívny layout (side-nav → bottom-nav/hamburger), touch-friendly ciele, tabuľky → karty na malých displejoch.
- Prístupnosť (WCAG AA), klávesové skratky, i18n-ready.

> **Musí obsahovať — AI agent v dashboarde**
- **Vstavaný AI agent** postavený na **Microsoft Agent Framework** (Microsoft.Extensions.AI + Semantic Kernel línia) — jednotná .NET abstrakcia nad LLM providermi, tool-calling, konverzačný stav.
- **Konfigurovateľné AI connection** — používateľ zadá pripojenie na model: *lokálne* (Ollama, LM Studio, foundry-local cez OpenAI-kompatibilné API) alebo *cloud* (Azure OpenAI, OpenAI, Anthropic, …). Kľúč/endpoint cez `ISecretStore` (§7.1), nikdy do descriptora. `Microsoft.Extensions.AI` dáva jednotné `IChatClient` rozhranie — provider je swappable rovnako ako ostatné porty (§1).
- **Čo agent robí:** „vytvor entitu vozidlá s poľami…" → návrh schémy + migrácie; generovanie rules/automation z prirodzeného jazyka; vysvetlenie chýb; dotazy nad dátami. Agent píše cez **to isté Management API** (§6, §2.6) ako CLI a dashboard — nie paralelná cesta (MCP je len voliteľný adaptér nad týmto API, §9); každý jeho zásah je auditovaný (§5) a podlieha RBAC dashboardu.
- **Bezpečnostná hranica agenta:** agent navrhuje, človek potvrdzuje (diff pred aplikáciou); deštruktívne operácie vyžadujú explicitné schválenie; agent nikdy neobchádza policy ani audit.

> **Pozor na**
- **Config drift:** všetko, čo sa dá naklikať (aj čo navrhne AI agent), musí byť exportovateľné ako kód (migrácia/descriptor) a verzovateľné — UI aj agent sú editory kódu, nie paralelná databáza konfigurácie. Zlaté pravidlo platí aj pre AI.
- **Admin bypass politík** je nutný, ale každá operácia (vrátane AI-generovanej) ide do audit logu.
- **WASM bundle size** ak sa zvolí WASM režim — lazy loading, trimming; inak prvý load bolí na mobile.
- **Náklady a latencia AI** — streaming odpovedí, lokálny model ako lacná/offline voľba, jasné označenie ktorý provider je aktívny; AI je asistent, nie povinná závislosť dashboardu (musí fungovať aj bez nakonfigurovaného modelu).

> **Akceptačné kritériá**
- Dashboard je plne ovládateľný na šírke 375 px (mobil) bez horizontálneho scrollu; kľúčové akcie dosiahnuteľné palcom.
- Vizuálny audit: dashboard neprejde, ak vyzerá ako default Bootstrap/Blazor template (vlastný design systém, dark/light).
- AI agent: prepnutie providera (lokálny ↔ OpenAI ↔ Anthropic) je len zmena connection v UI, žiadny zásah do kódu; kľúč je v secret store, nie v logoch ani descriptore.
- Každá AI-navrhnutá zmena schémy/rules sa zobrazí ako diff a aplikuje až po potvrdení; zápis je auditovaný.

> **.NET stavebné bloky**
**Blazor Web App** (.NET 10, server interactive default). Komponenty: **MudBlazor** / **Radzen** / **FluentUI-Blazor** pretémované na vlastný design systém. AI: **Microsoft Agent Framework** + **Microsoft.Extensions.AI** (`IChatClient`) s providermi Azure OpenAI / OpenAI / Anthropic / Ollama (lokálne); kľúče cez `ISecretStore`. Agent operuje cez Management API (§6); MCP je voliteľný adaptér nad ním pre externých agentov (§9).

### 2.9 Client SDKs

> **Musí obsahovať**
- Fluentné query API zrkadliace serverový query language; **typový codegen zo schémy** (ekvivalent `generate_typescript_types` — pre .NET: generované C# POCO + typované query buildery, compile-time kontrola).
- **Žiadna povinná base class** pre modely (poučenie z frikcie supabase-csharp `BaseModel`) — plain POCO + System.Text.Json.
- Auth klient s pluggable token storage; realtime klient s automatickým reconnectom (exponenciálny backoff + jitter) a resume sémantikou; storage klient s TUS.
- Jednotný error model (mapovanie RFC 7807 na typované výnimky); offline stratégia aspoň dokumentovaná (Firebase tu drží latku).

> **Pozor na**
- Verzovanie SDK vs server API — kontraktné testy medzi SDK a serverom v CI; deprecation policy.
- Supabase C# klient je komunitný a *„môže zaostávať za oficiálnym JS klientom"* (vlastné slová Supabase docs) — presne túto dieru .NET-native framework rieši first-party SDK.

### 2.10 Vector / AI features

Supabase má **pgvector** natívne — embeddingy vedľa relačných dát, čo zjednodušuje RAG a semantic search; dnes významná výhoda oproti Firebase. **Musí obsahovať:** vector stĺpce ako first-class typ v schéme aj API (KNN operátory vo filter jazyku), hybrid search (FTS + vector), a — všimni si prepojenie na §3 — *automatická synchronizácia embeddingov*: „pri zmene stĺpca `content` prepočítaj embedding" je presne automation rule. **Pozor na:** embedding provider musí byť pluggable (OpenAI/Azure/lokálny model), náklady na re-embedding pri bulk zmenách (coalescing!). Pre .NET: `pgvector-dotnet` + Npgsql integrácia.

### 2.11 Caching

**Čo to je:** vrstva, ktorá znižuje latenciu a odľahčuje databázu tým, že drží často čítané dáta bližšie k požiadavke. V BaaS je zradná, lebo naráža priamo na autorizáciu (§2.4) a realtime (§2.5): cache nesmie obísť row-level policy a nesmie servovať zastaralé dáta po zmene. Väčšina BaaS caching buď nemá (spolieha na Postgres), alebo ho necháva na klienta/CDN — je to jedna z mála oblastí, kde dobre navrhnutý framework môže reálne pridať hodnotu.

> **Musí obsahovať**
- **HTTP caching na hrane:** korektné `ETag` / `Last-Modified` + `Cache-Control`; podpora podmienených requestov (`If-None-Match` → 304). Pre *per-user* dáta `Cache-Control: private`, pre verejné `public` s krátkym TTL. Toto je najlacnejšia a najbezpečnejšia úroveň.
- **Server-side cache za rozhraním `ICacheStore`** (§1) — dvojvrstvová: L1 in-memory (per instancia) + L2 distribuovaná (Redis/Valkey); v .NET presne model **HybridCache** (.NET 10), ktorý rieši aj stampede (single-flight na cache miss).
- **Cache key musí zahŕňať autorizačný kontext** — user/tenant/role sú súčasťou kľúča, inak cache leakne dáta medzi užívateľmi. Alternatíva: cacheuj až *po* policy filtri, alebo cacheuj len policy-agnostické dáta (číselníky, konfigurácia).
- **Event-driven invalidácia (napojenie na §3):** zmena entity emituje event → invalidácia dotknutých cache kľúčov (tag-based invalidation). Toto je správny spôsob — TTL-only cache buď servuje staré dáta, alebo je TTL taký krátky, že cache nemá zmysel.
- **Query result cache** pre drahé read-heavy dotazy (opt-in per endpoint/entita), s explicitným TTL a tag invalidáciou.
- **Negatívne cachovanie** (404/prázdne výsledky) proti cache-penetration útokom.

> **Čo je štandard**
CDN + HTTP caching na hrane je univerzálny štandard (Supabase Smart CDN pre storage). Redis ako distribuovaná cache je de-facto default; v .NET je **HybridCache** oficiálna abstrakcia (L1+L2, stampede protection, tag invalidation) a beží nad ľubovoľným `IDistributedCache` providerom — čiže sadne presne do provider modelu (§1). PostgREST/Supabase spoliehajú prevažne na Postgres a HTTP caching; dedikovaný app-level cache s event invalidáciou je diferenciátor.

> **Pozor na**
- **Cache ako authorization bypass** — najnebezpečnejšia chyba: user A dostane cachnutú odpoveď usera B. Kľúč musí byť tenant+user+role-aware, alebo cacheuj len po policy filtri.
- **Zastaralé dáta vs realtime** — ak appka zároveň dostáva realtime update a číta cache, musia byť konzistentné; event invalidácia musí prebehnúť pred/súčasne s realtime pushom.
- **Cache stampede** (thundering herd na expiráciu horúceho kľúča) — single-flight/HybridCache; **tenant izolácia** v shared cache (prefix kľúča tenantom, aby jeden tenant nevytlačil dáta iného).
- **Invalidácia je ťažká** — pri relačných dátach jedna zmena zneplatní viacero odvodených kľúčov; tag-based invalidation a konzervatívne cachovanie (radšej menej) sú bezpečnejšie než agresívne TTL.

> **Akceptačné kritériá**
- Two-user test: cachnutá odpoveď sa nikdy neservuje inému užívateľovi/tenantovi (fuzzing kľúčov).
- Zmena riadku zneplatní cache do X ms; následné čítanie vráti novú hodnotu (nie stale).
- Concurrent burst na expirujúci horúci kľúč spustí práve jeden DB dotaz (stampede test).
- Prepnutie `ICacheStore` z Redis na in-memory nevyžaduje zmenu kódu.

### 2.12 Observability & telemetry, rate limiting, API keys

**Čo to je:** schopnosť vidieť, čo sa v systéme deje — pre debugging, výkon, bezpečnosť aj billing. V BaaS je kritickejšia než v bežnej appke, lebo prevádzkovateľ ladí *cudzí* kód (automation rules, funkcie, dotazy koncových užívateľov) a musí vedieť rozlíšiť „chyba frameworku" od „chyba užívateľovho pravidla".

> **Musí obsahovať**
- **OpenTelemetry ako natívny základ** (traces + metrics + logs), export za rozhraním `ITelemetrySink` (§1): Application Insights na Azure, OTLP → Grafana/Tempo/Prometheus/Jaeger inde, konzola v dev. Nikdy neviaž framework na konkrétny backend.
- **Distribuovaný trace end-to-end:** jeden `trace-id` tečie API → policy eval → DB dotaz → event → automation rule → funkcia → webhook. Bez toho sa reťazenie (§3) nedá debugovať.
- **Framework-specific metriky:** rule-eval time, change-feed/replication-slot lag, webhook delivery rate & latencia, outbox queue depth, cache hit ratio, auth failure rate — nie len generické HTTP metriky.
- **Štruktúrované logy** (nie string concat) s korelačným id a úrovňami; per-execution logy pre funkcie aj automation rules (§3) dostupné v admin UI.
- **Rate limiting** viacúrovňový (per API key / user / tenant / IP), token-bucket alebo sliding window, s `Retry-After` a `X-RateLimit-*` hlavičkami; prísnejšie limity na auth a write; konfigurovateľné per tenant (§4).
- **API keys management:** scopes (least privilege), expirácia, rotácia bez výpadku (prekryv dvoch platných), viditeľné „last used", okamžitá revokácia; oddelené klientske vs server (service) kľúče.
- **Usage metering** — počty requestov, storage GB, realtime konexie, function/rule executions, egress; základ pre quoty, alerting aj prípadný billing. Per tenant.
- **Health & SLO:** liveness + readiness (DB, cache, message bus reachability); definované SLI/SLO a alerty.

> **Čo je štandard**
OpenTelemetry je dnes univerzálny štandard (vendor-neutral, .NET má prvotriednu podporu vrátane auto-inštrumentácie ASP.NET Core, Npgsql, HttpClient). Supabase ponúka log export a metriky na Pro tarife (Datadog/Grafana). Retry-After + štruktúrované rate-limit hlavičky sú webový štandard.

> **Pozor na**
- **PII v telemetrii** — telá requestov, query parametre a claims môžu obsahovať osobné údaje; scrubbing/redaction by default, opt-in pre detailné logy.
- **Log retention** — Supabase drží webhook responses len 6 h (v praxi málo); sprav retenciu konfigurovateľnou a napojenou na provider (lacný cold storage pre dlhú retenciu).
- **Kardinalita metrík** — tenant-id / user-id ako label metriky vyhodí Prometheus do kolien; používaj exemplars/tracing na high-cardinality dimenzie, nie labely.
- **Náklady telemetrie** — full tracing na 100 % trafficu je drahé; sampling s tail-based zachytením chýb.
- **Rate limit v distribuovanom nasadení** — per-instance limity sa násobia počtom inštancií; distribuovaný counter (Redis) alebo centrálny gateway.

> **Akceptačné kritériá**
- Jeden request cez API → rule → webhook má súvislý trace s jedným trace-id vo všetkých spanoch.
- Prepnutie `ITelemetrySink` (App Insights → OTLP) nevyžaduje zmenu kódu.
- PII scrubbing overený testom (citlivé polia sa neobjavia v exportovaných logoch).
- Rate limit vráti 429 + Retry-After; usage metering súhlasí s reálnym počtom operácií (±0 v teste).

### 2.13 Migrations, branching, preview environments

> **Musí obsahovať**
- **Migrácie ako kód v repe** — verzované, s checksumami, idempotentne aplikovateľné; systémová schéma frameworku sa migruje automaticky pri štarte.
- **Deklaratívna schéma + diff** — vývojár (alebo agent) edituje želaný stav, framework vygeneruje migráciu (Supabase declarative schemas / Atlas model). Pre agentov kľúčové: deklaratívny súbor je jednoduchší na správne vygenerovanie než sekvencia ALTERov.
- **Runtime/dashboard-first migračná cesta — jeden diff engine, dva zdroje želaného stavu.** Doteraz uvedené je code-first (schéma ako súbor v repe, diff pri builde/deployi). Ale descriptor je *formát, nie miesto* (§9.2): v runtime režime žije želaný stav ako **záznam v DB** a mení sa *za behu* cez dashboard/Management API na bežiacej inštancii nad reálnymi dátami. Migračný mechanizmus musí byť **ten istý declarative-diff engine** s dvomi vstupmi — súbor v repe alebo záznam v DB — nie dva oddelené systémy. Tok: zmena v dashboarde → nový descriptor (nový želaný stav) → diff proti introspekcii fyzickej DB → vygenerovaná migrácia → aplikovaná v transakcii s *rovnakými* guardrails na deštruktívne zmeny (DROP/type change → explicitné potvrdenie + dry-run). Bez toho ostane jeden z dvoch primárnych režimov (dashboard-first) len polovičný.
- **Verzovanie descriptora v DB (runtime náhrada za git).** V code-first ceste rieši verzovanie, audit a rollback git zadarmo. V runtime ceste git nie je — preto descriptor v DB musí byť **append-only verzovaný** (kto/kedy/čo zmenil = história zmien schémy), inak niet auditu ani rollbacku. **Rollback** („vráť descriptor na verziu N") generuje spätnú migráciu — ťažšie než `git revert`, lebo DROP stĺpca už dáta zmazal (guardrail to musí zachytiť dopredu). **Concurrency:** dvaja admini menia schému naraz cez dashboard — optimistic locking na descriptore (verzia X→X+1, konflikt = odmietni), git-štýl merge tu nie je.
- **Export runtime → kód (most, nie slepá ulička).** Runtime descriptor z DB musí ísť kedykoľvek vyexportovať do súboru (§9.2 bidirectional bridge), aby prechod z dashboard-first na GitOps bol možný — dashboard-first nesmie byť jednosmerná pasca.
- **Guardrails na deštruktívne zmeny** — DROP/column type change vyžaduje explicitný flag; dry-run výstup „čo sa stane". Platí rovnako pre code-first aj runtime cestu (v runtime o to viac — beží nad živými dátami).
- **Seed dáta** ako súčasť dev loopu; drift detection (DB vs repo).
- **Kohabitácia systémovej schémy v embedded modeli** — pri NuGet nasadení do existujúcej appky (ERP scenár) žijú systémové tabuľky frameworku v *cudzej* databáze vedľa tabuliek hostiteľa: vyhradený prefix/schéma (`baas.*`), žiadne kolízie názvov, vlastný nezávislý migračný reťazec (framework si migruje svoje tabuľky pri štarte bez dotyku hostiteľových), a jasne definovaný upgrade/downgrade kontrakt medzi verziou NuGet balíka a verziou systémovej schémy.
- **Verzovanie samotného frameworku** — pre knižnicu-first produkt je verejné C# API (rozhrania §1, extension pointy §10) kontrakt voči hostiteľským appkám: SemVer, breaking changes len v major verzii, dokumentovaná deprecation policy. Nezamieňať s verzovaním REST API pre koncových integrátorov (§6).

> **Čo je štandard**
Supabase branching: každý PR = izolovaná DB + API preview, ale **kopíruje len schému, nie dáta** (nutný seed). Neon branching je copy-on-write *s dátami* — pre testovanie migrácií na realistických dátach výrazne lepší a je to latka, ku ktorej sa oplatí mieriť (na Postgse dosiahnuteľné cez template databases / snapshoty pre menšie DB).

### 2.14 Self-hosting & deployment model — dva primárne režimy Alvo

> **Režim 1 — Standalone (Docker image)**
Stiahneš `mmlib/alvo` image, spustíš, otvoríš **dashboard** — vytvoríš nový projekt, nastavíš, máš backend. Konfigurácia kontajnera cez env premenné (admin meno/heslo, DB connection, provideri), ale rovnako môžeš rovno **podhodiť project descriptor** (deklaratívny JSON balík s definíciou backendu — entity, rules, automation, auth) a kontajner naštartuje s hotovým projektom bez klikania. Pre agentov a CLI: **Management API** — všetko, čo vie dashboard, vie aj API (a teda aj `alvo` CLI; MCP je voliteľný adaptér nad API pre externých agentov). „Projekt" v standalone režime = **jedna databáza per projekt** (v dev SQLite súbor per projekt) — čistá izolácia na úrovni platformy, nezávislá od multi-tenancy (§4), ktorá žije *vnútri* projektu.

> **Režim 2 — Embedded (NuGet v tvojom hoste)**
Pripneš `MMLib.Alvo` do vlastného ASP.NET Core projektu, ktorý je host. Konfigurácia cez C# (fluent builder), plná extensibility: vlastné moduly, custom autorizácie, hooks, endpointy, providery — komplexné možnosti, ktoré deklaratívny režim nemá. Presne ERP scenár (§0): Alvo zabudované v existujúcej platforme.

> **Kľúčové architektonické väzby medzi režimami**
- **Jeden kód:** standalone image je len predpripravený host (`Alvo.Host`), ktorý interne používa presne ten istý NuGet ako režim 2. Docker = „hosting za teba", nie iný produkt.
- **Project descriptor je jednotný artefakt:** mountneš ho do Dockera, aplikuješ cez CLI/API, exportuješ z admin UI, alebo načítaš embedded cez `AddAlvo().FromDescriptor(...)`. Jeden formát = GitOps, agent-friendly, a zároveň **migračná cesta standalone → embedded** (vyrastieš z Dockera → zoberieš descriptor do vlastného hostu bez prepisovania). **Pozor na zdroj pravdy:** descriptor je jeden *formát*, ale schéma môže *žiť* buď ako súbor v repe (GitOps — zdroj pravdy Git), alebo ako záznam v DB bežiacej inštancie (dashboard-first — vytvoríš projekt a pridávaš entity za behu, uložené v schema registry §2.1, žiadny súbor). Most je obojsmerný export/import; nie je to „všetko sú súbory", je to „jeden formát, dva zdroje pravdy".
- **Descriptor ≠ infra config:** descriptor definuje backend (entity, rules, automation); env premenné definujú infraštruktúru (heslá, connection stringy, provideri §1). Nemiešať.
- **Jedno Management API:** dashboard aj CLI sú klienti toho istého API — žiadne divergentné konfiguračné cesty. MCP je voliteľný adaptér nad tým istým API (§9), nie samostatná cesta.
- **Hranica extensibility ako feature:** standalone = deklaratívna extensibility (rules, automation, webhooky, descriptor) + **csx skripty** pre custom logiku (trust: admin kód — §2.6). Keď potrebuješ kompilované moduly, vlastné providery, plný DI a vlastnú architektúru — to je signál na prechod do embedded režimu. Jasný upgrade path, nie obmedzenie.

#### Single binary — PocketBase

Jeden Go executable + SQLite. Strop: jeden stroj. Zlatý štandard jednoduchosti.

#### Docker Compose — Supabase

~7 služieb okolo Postgres (Kong, GoTrue, PostgREST, Realtime, Storage, Functions, Studio). Plná sila, ťažšia prevádzka.

#### Embedded knižnica — medzera

NuGet balík v existujúcej ASP.NET Core appke (`app.MapBaaS()`). Nikto to nemá; prirodzený .NET model.

#### Cloud + free self-host

Dominantný biznis model kategórie (Supabase, Appwrite, Convex po open-sourcingu 2024).

> **Akceptačné kritériá (deployment)**
- `docker run mmlib/alvo` = funkčný backend s dashboardom do 60 s bez akejkoľvek konfigurácie (SQLite); s namountovaným descriptorom naštartuje s hotovým projektom bez zásahu do UI.
- Ten istý project descriptor prejde všetkými štyrmi cestami (mount / CLI / Management API / `FromDescriptor()` v embedded) s identickým výsledkom.
- Upgrade path: nová verzia migruje systémovú schému automaticky a bezpečne; downgrade dokumentovaný.
- Ten istý kód beží embedded (NuGet) aj standalone (kontajner) — jeden codebase, dva distribučné modely; export descriptora zo standalone a jeho import do embedded hostu zachová 100 % definície backendu.
- Admin bootstrap bez default credentials: heslo cez env/secret alebo first-run wizard; image nikdy nedodáva prednastavené prihlásenie.

### 2.15 Messaging & notifications (email / SMS / push)

**Čo to je:** odchádzajúca komunikácia s koncovými užívateľmi cez email, SMS a push notifikácie. Appwrite z toho spravil first-class produkt (Messaging, od v1.5) s jednotným API nad 10+ providermi; Supabase/Firebase to riešia cez funkcie + externé služby. Pre BaaS je to dôležité, lebo transakčné maily (verification, reset, invoice, order confirmation) sú súčasťou auth aj automation flow — a nikto ich nechce lepiť ručne.

> **Musí obsahovať**
- **Jednotné API nad kanálmi** za rozhraniami `IEmailSender` / `ISmsSender` / `IPushSender` (§1) — jedno volanie, provider sa vyberá podľa prostredia a kanála.
- **Koncept „targets" a „topics"** (Appwrite model): target = konkrétny email/telefón/zariadenie užívateľa; topic = skupina odberateľov pre hromadné správy (newsletter, announcement). Napojené na užívateľov z auth (§2.2).
- **Šablóny** s premennými, lokalizáciou a náhľadom; oddelenie obsahu od doručenia.
- **Napojenie na automation (§3):** „send email/push" je built-in akcia rule enginu — `user.registered → verification email`, `order.approved → confirmation` — deklaratívne, nie kódom.
- **Scheduling** (odoslať neskôr) a **draft → processing → sent/failed** stavový model s logmi per správa (Appwrite model); okamžité aj naplánované doručenie.
- **Delivery tracking** — status per target, bounces, retries cez outbox (§3), dead-letter; delivery log s retenciou.
- **Multi-provider per kanál** — viac email providerov s fallbackom/routingom (Appwrite to umožňuje: vyber providera per správu).

> **Čo je štandard**
Providery: email = SendGrid / Mailgun / SMTP / Azure Communication Services; SMS = Twilio / Vonage / MSG91 / Telesign; push = FCM (Android/web) + APNs (Apple) / Azure Notification Hubs. Appwrite Messaging je referenčný jednotný model (targets, topics, providers, scheduling). Transakčné maily musia byť oddelené od marketingových (iná reputácia domény, iný opt-out režim).

> **Pozor na**
- **Deliverability** — SPF/DKIM/DMARC; transakčné a marketingové maily z rôznych subdomén, inak si zničíš doručiteľnosť verification mailov.
- **Compliance opt-out** — GDPR/CAN-SPAM: unsubscribe pre marketing, consent tracking; push notifikácie potrebujú platný token lifecycle (expirácia, cleanup mŕtvych tokenov).
- **Idempotencia** — retry doručenia nesmie poslať mail dvakrát (dedup cez message id); pending správy musia prežiť reštart (outbox, nie in-memory fronta — Appwrite self-host ich pri reštarte stráca).
- **PII a šablóny** — telá správ obsahujú osobné údaje; logovanie s redaction, retencia obsahu obmedzená.
- **Rate limits providerov** a náklady — throttling a batch odosielanie (Appwrite worker posiela v batchoch per provider).

> **Akceptačné kritériá**
- Automation akcia „send email" pri `user.registered` doručí práve jeden mail (idempotencia pri retry).
- Prepnutie `IEmailSender` (SendGrid → SMTP → ACS) nevyžaduje zmenu kódu ani šablón.
- Naplánovaná správa sa odošle v definovanom čase; zlyhané doručenie ide do DLQ s dôvodom a je viditeľné v logu.
- Pending správy prežijú reštart procesu (persistentná fronta).

## §3 Automation, rule engine a webhooky

„Keď vznikne/zmení sa/zmaže sa záznam a spĺňa podmienku → vykonaj akciu." Toto je vrstva, ktorá z CRUD databázy robí *backend* — notifikácie, integrácie, workflow, denormalizácie, sync do externých systémov. A zároveň vrstva, kde má konkurencia najviac dier, takže je to reálna šanca na diferenciáciu.

### 3.1 Ako to riešia konkurenti — a kde majú diery

| Platforma | Model | Silné stránky | Diery |
|---|---|---|---|
| **Supabase**  Database Webhooks | DB triggery + `pg_net` (async HTTP z Postgresu). Payload: `{type: INSERT|UPDATE|DELETE, table, schema, record, old_record}`. Doplnok: `pg_cron` pre schedule. | Jednoduché, transakčne konzistentné (trigger v DB), non-blocking (async worker), definovateľné v SQL migráciách. | Žiadny condition layer (filter „len keď status = approved" si píšeš ručne v plpgsql), žiadne retries/DLQ out of the box, response log len 6 hodín, pg_net worker vie spadnúť a treba ho reštartovať, dokumentované mass-timeouty pri náporoch, riziko infinite loop pri triggeroch na net tabuľkách. |
| **Appwrite**  Events + Webhooks | Jednotný event katalóg s wildcard syntaxou (`users.*.create`, `tablesdb.*.tables.*.rows.*.update`); konzumenti: Functions, Realtime, Webhooky. Webhooky s HMAC signing secretom a zero-downtime rotáciou kľúča. | Najčistejší event katalóg v kategórii; jeden event systém poháňa realtime aj webhooky aj funkcie (elegantná architektúra). | **Nevie podmienku na zmenu atribútu** — „spusti len keď sa `status` zmenil na approved" sa nedá; komunita odporúča workaround s duplicitnou kolekciou na porovnávanie (reálny thread). Self-host: pending webhooky sa pri reštarte kontajnerov *stratia*. Ochrana pred rekurziou je natvrdo (funkcia nemôže triggernúť funkciu). |
| **Directus**  Flows | Plný ECA model: trigger (event hook / inbound webhook / cron / iný flow / manuálny gombík) → operations chain s **condition** (filter rules), CRUD, email, HTTP request, transform, script; data chain (`$trigger`, `$last`, `$accountability`); resolve/reject vetvenie; **blocking Filter** (môže transformovať alebo vetovať transakciu) vs **non-blocking Action**. | Najprepracovanejší automation model v kategórii — benchmark pre návrh. Vizuálny builder + logy per run. | Bulk operácie triggerujú flow *per item* (10k insertov = 10k behov); dokumentovaná sťažnosť: tisíce log záznamov flow, ktoré sa hneď abortnú na podmienke, robia debugging nemožným (podmienka sa vyhodnocuje až *po* spustení flow, nie pred). Condition s chýbajúcim fieldom ide do reject vetvy — prekvapivá sémantika. Licencia MSCL (nie OSS). |
| **Firebase**  Cloud Functions triggers | Najzrelšie event triggery: Firestore write, Auth event, Storage upload, Pub/Sub, scheduled. Eventarc pod kapotou. | Obrovská paleta zdrojov eventov (celý GCP ekosystém). | Podmienky = kód vo funkcii (platíš za invokácie, ktoré hneď skončia); cold starts; žiadne deklaratívne rules bez kódu; vendor lock-in. |
| **PocketBase**  Hooks | 70+ hook points v Go/JSVM (onRecordCreate, before/after…), plná programová kontrola. | Maximálna flexibilita pre programátora. | Žiadna deklaratívna vrstva — všetko je kód; žiadny managed webhook delivery (retries, signing si píšeš sám). |

> **Syntéza**
Nikto nemá súčasne: (a) deklaratívny ECA model Directusu, (b) čistý event katalóg Appwritu, (c) transakčnú konzistenciu Supabase triggerov a (d) enterprise-grade webhook delivery (retries, signing, DLQ, redelivery). To je dizajnový cieľ.

### 3.2 Základ: unified event system

**Čo to je:** chrbtová kosť. Všetko v systéme emituje eventy do jedného katalógu a všetko ostatné (realtime, webhooky, funkcie, automation rules, audit) sú *konzumenti toho istého streamu* — architektúra, ktorú má Appwrite („realtime is built on top of our internal events system") a ktorá zaručuje, že nová feature automaticky dostane eventy zadarmo.

> **Musí obsahovať**
- **Event katalóg s konvenciou pomenovania** `resource.action` a wildcardmi: entity eventy (`entity.orders.created|updated|deleted` — s `record` aj `old_record`), auth eventy (`user.registered`, `user.login`, `user.password_changed`), storage eventy (`object.created|deleted`), function eventy (`function.X.completed|failed`) a **custom app eventy** emitované z kódu (`ctx.Publish("order.approved", payload)`) — bez custom eventov skončíš ako Directus, kde ľudia počúvajú na generický UPDATE a filtrujú tisíce falošných spustení.
- **Štandardizovaná obálka** — CloudEvents (CNCF spec): `id` (dedup), `source`, `type`, `time`, `subject`, `data`. Jedna obálka pre interné aj externé doručenie.
- **Transactional outbox:** event sa zapíše do outbox tabuľky *v tej istej transakcii* ako dátová zmena; dispatcher ho po commite publikuje. Toto je jediný spôsob, ako zaručiť „žiadna zmena bez eventu, žiadny event bez zmeny" — dual-write (zápis do DB + publish do queue mimo transakcie) skôr či neskôr stratí alebo zduplikuje eventy.
- **Provenance metadata** na evente: kto ho spôsobil (user/service/automation X), korelačné id, **hĺbka reťazenia** (pre loop protection).
- **Changed-columns informácia** pri UPDATE — zoznam zmenených stĺpcov priamo v evente, aby podmienky „zmenil sa X" boli lacné.

### 3.3 Rule engine: event → condition → action

**Čo to je:** deklaratívna vrstva, kde si užívateľ (v UI alebo v descriptore v repe — obe reprezentácie toho istého) definuje automation rules. JSON je kanonický formát (§3, ladí s `alvo-descriptor.schema.json` aj §16); komentáre nižšie sú len vysvetlenie, nie súčasť súboru. Anatómia pravidla:

```json
{
  "name": "notify-approved-orders",
  "trigger": { "event": "entity.orders.updated" },   // aj wildcard: entity.orders.*
  "condition": "changed(status) && new.status == 'approved' && new.total > 1000",
  "actions": [
    { "type": "webhook", "endpoint": "erp-integration" },   // referencia na spravovaný endpoint, nie inline URL
    { "type": "function", "name": "send-approval-email" },
    { "type": "entity.update",                              // data akcia: create/update záznamu
      "entity": "audit_trail",
      "payload": { "order_id": "{{new.id}}", "event": "approved" } }
  ]
}
```

Condition používa **ten istý CEL** ako authorization rules; payload transformácie (`{{...}}`) sú JSONata. Descriptor je vždy JSON (jeden formát, jeden parser); export, API aj JSON Schema pracujú s JSON.

> **Musí obsahovať**
- **Triggery:** event (s wildcardmi), inbound webhook (vygenerovaná URL s auth — Directus model; pokrýva „Stripe mi pošle event"), schedule (cron + one-shot delay), manuálny (gombík v UI s potvrdením a vstupnými poľami), iný rule (reťazenie flows).
- **Condition ako súčasť subscription, nie prvý krok behu.** Podmienka sa vyhodnotí *pred* spustením rule — inak vznikne Directus problém (tisíce logov abortnutých behov). Výrazy nad `new`, `old`, `changed()`, `@user`/`@actor`; presne definovaná sémantika chýbajúceho fieldu (null-safe operátory, nie tiché reject).
- **Jeden jazyk pre podmienky, jeden pre transformácie (rozhodnuté):** podmienky — authorization rules (§2.4), hook/automation conditions, computed fields, storage policies — používajú **CEL podmnožinu** (Common Expression Language: non-Turing-complete, safe-by-construction, syntax známa agentom z Kubernetes/Envoy sveta) s vlastným compilerom do (a) parametrizovaného SQL predikátu a (b) in-memory delegátov; rozšírenia `changed(field)`, `old/new`, `@user/@tenant`. Transformácie payloadov (webhook body, akcie) používajú **JSONata** (vzor AWS Step Functions), s `{{...}}` šablónami ako cukrom — JSONata je Turing-complete, preto *nikdy nebeží in-transaction* (before-hooky a rules = len CEL) a evaluátor má depth/time limity. Descriptor formát: **JSON, jediný** (JSON Schema, spoľahlivá generácia agentmi) — žiadny YAML/JSONC.
- **Akcie (built-in katalóg):** call webhook, run function, send email/push (pluggable provider), create/update/delete record (s definovanou identitou: ako system/ako pôvodca — Directus `$full`/`$trigger` model), emit custom event, enqueue job, delay/sleep, condition branch, transform (mapovanie payloadu šablónou), log, throw (abort).
- **Lifecycle hooks — before/after per operácia, v dvoch tvárach (viazané na režimy §2.14):**

**Hook pointy:** `beforeCreate / afterCreate / beforeUpdate / afterUpdate / beforeDelete / afterDelete` per entita (PocketBase vzor, 70+ hook points), plus auth a storage ekvivalenty (`beforeLogin`, `afterUpload`…).
*Before-hooks (blocking, in-transaction):* validácia, transformácia payloadu, **veto/zrušenie operácie**. Rozpočet v ms, **zákaz sieťových volaní**, beží synchronne pred commitom. (Directus „Filter", PocketBase before-hooks.)
*After-rules (non-blocking, post-commit):* všetko ostatné. Bežia z outboxu, durable, s retries. **Železné pravidlo: žiadne externé volanie pred commitom transakcie.**
**Deklaratívna tvár (standalone aj embedded):** hook definovaný v descriptore/UI cez expression language — condition + akcie z built-in katalógu, vrátane `reject("dôvod")` (zruší operáciu s RFC 7807 chybou) a `mutate(pole, hodnota)` (upraví payload pred zápisom).
**C# tvár (embedded host):** registrácia typovaného hooku v kóde hostu — plná programová logika (prístup k DI, službám hostu, externým systémom v after-hookoch). Obe tváre bežia cez ten istý pipeline a tie isté pravidlá (before = bez siete, rozpočet; after = durable).
- **Loop protection:** provenance hĺbka na evente (rule A → zápis → event → rule B → … max N, default ~5) + detekcia cyklu + alert. Lepšie než Appwrite plošný zákaz (function nesmie triggernúť function), ktorý blokuje legitímne reťazenie.
- **Coalescing/batching pri bulk operáciách:** import 10k riadkov nesmie znamenať 10k webhookov — rules deklarujú, či chcú per-item alebo batch doručenie (event `entity.orders.created.batch` s poľom záznamov). Directus per-item správanie je dokumentovaný škálovací problém.
- **Observabilita:** execution log per beh (trigger payload, výsledok každej akcie, trvanie), korelovaný request-id, metriky (behy/s, fail rate, lag), retencia konfigurovateľná. A kľúčové: *podmienkou odfiltrované eventy sa nelogujú ako behy* (iba counter).
- **Test/dry-run:** „prehraj tento historický event proti pravidlu a ukáž, čo by sa stalo" — obdoba policy simulatora.

> **Pozor na**
- **At-least-once znamená duplikáty** — každá akcia musí byť idempotentná alebo dedupovaná cez event id; data akcie (create record) potrebujú idempotency key odvodený z event id.
- **Ordering:** negarantuj globálne poradie (drahé a krehké); garantuj per-entity-key ordering, ak sa dá (partition podľa PK), a dokumentuj to.
- **Multi-tenancy izolácia** pravidiel — tenant vidí a triggeruje len svoje; system rules oddelené.
- **Payload PII do externých webhookov** — voľba thin payload (event + id, konzument si dofetchne cez API s vlastným kľúčom) vs full payload; per-endpoint nastavenie.
- **Backpressure:** mŕtvy webhook target nesmie zahltiť dispatcher — per-endpoint queue s limitom, circuit breaker.

### 3.4 Outbound webhooky ako produkt

**Čo to je:** spravované HTTP doručovanie eventov tretím stranám. Rozdiel medzi „vieme poslať POST" (Supabase pg_net) a webhookmi ako produktom (Stripe kvalita) je presne v tomto zozname:

> **Musí obsahovať**
- **Spravované endpointy:** URL + secret + zoznam subscribed eventov (wildcardy) + aktívny/pozastavený stav. Endpoint je entita s vlastným lifecycle, nie inline URL v pravidle.
- **Podpisovanie: HMAC-SHA256** nad (timestamp + payload), timestamp v hlavičke proti replay útokom; **zero-downtime rotácia secretu** (obdobie dvoch platných kľúčov — Appwrite `updateSecret` model). Adoptuj **Standard Webhooks** špecifikáciu (standardwebhooks.com — Svix a spol.): štandardizované hlavičky `webhook-id`, `webhook-timestamp`, `webhook-signature`, hotové verifikačné knižnice pre konzumentov vo všetkých jazykoch zadarmo.
- **Delivery sémantika: at-least-once** s retries — exponenciálny backoff + jitter (napr. 5 s → 30 s → 2 min → 15 min → 1 h → 6 h), konfigurovateľný počet pokusov, **dead-letter** po vyčerpaní + alert + automatická pauza endpointu po X po sebe idúcich zlyhaniach (s notifikáciou vlastníkovi).
- **Delivery log:** každý pokus so status kódom, latenciou, response telom (skráteným), konfigurovateľná retencia (Supabase 6 h je málo). **Manuálna redelivery** z UI/API — jednotlivo aj bulk replay za časové okno.
- **2xx = úspech, všetko ostatné retry;** timeout per endpoint (default ~10–30 s); `webhook-id` pre dedup na strane konzumenta.
- **Bezpečnosť odosielateľa:** HTTPS only (opt-out len pre localhost dev), voliteľná SSRF ochrana (zákaz privátnych IP rozsahov — webhook URL zadáva užívateľ a smie ňou skúsiť trafiť tvoju internú sieť!), voliteľné statické egress IP pre enterprise allow-listy.
- **Payload verzovanie** — schéma event payloadu sa bude vyvíjať; verzia v obálke od prvého dňa.

> **Čo je štandard**
**CloudEvents** (CNCF) pre obálku eventu, **Standard Webhooks** pre doručovanie a podpisovanie — obe majú .NET podporu (balík `CloudNative.CloudEvents`; Standard Webhooks má referenčné implementácie). Adopcia štandardov = agenti a integrátori poznajú formát z trénovacích dát a existujúcich integrácií.

> **Akceptačné kritériá (celý §3)**
- **Crash test outboxu:** kill procesu medzi commitom a publishom → po reštarte je event doručený; kill uprostred akcie → akcia sa zopakuje; žiadny stratený event v 10k-eventovom chaos teste.
- **Podmienka na zmenu stĺpca funguje:** `changed(status) && new.status == 'approved'` triggerne práve raz, pri prechode — nie pri každom update (Appwrite gap test).
- Webhook konzument s referenčnou Standard Webhooks knižnicou verifikuje podpis; replay so starým timestampom je odmietnutý.
- Endpoint down 2 h → eventy sa doručia po oživení v správnom per-key poradí; po vyčerpaní retries sú v DLQ a bulk redelivery ich doručí.
- Bulk insert 10k riadkov s batch pravidlom = 1 batch event; s per-item pravidlom systém doručuje s backpressure bez degradácie API latencie.
- Umelý cyklus (rule → zápis → ten istý rule) je zastavený na hĺbke N s alertom, nie nekonečnou slučkou.
- Blocking before-hook prekročí rozpočet → transakcia sa čisto rollbackne s RFC 7807 chybou.

> **.NET stavebné bloky**
**Wolverine** (JasperFx, MIT) — durable messaging s natívnym **transactional outbox** patternom, retries a scheduled delivery: presne jadro tejto sekcie, hotové. **Hangfire / Quartz.NET** pre cron a delayed jobs. **CloudNative.CloudEvents** pre obálku. **Polly** (súčasť .NET resilience) pre HTTP retry/circuit breaker na webhook delivery. **System.Threading.Channels** pre in-proc pipeline dispatchera. Expression engine — ten istý, ktorý poháňa authorization rules (§2.4): jeden parser, kompilácia do SQL (podmienky nad dátami) aj do delegátov (podmienky nad event payloadom).

## §4 Multi-tenancy

**Čo to je:** schopnosť jednej inštancie obslúžiť viacero navzájom izolovaných zákazníkov (tenantov) s garanciou, že tenant A nikdy neuvidí dáta tenanta B. Pre framework, ktorého cieľová skupina sú tímy stavajúce SaaS (§12.2), je to *nosná* vlastnosť, nie doplnok. Žiadny mainstream BaaS to nemá ako first-class koncept — všetci to nechávajú na ručnú RLS gymnastiku. Toto je jeden z hlavných diferenciátorov.

> **Rozsah v0.1 — jeden model, abstrakcia pripravená na ďalšie**
> **Rozhodnutie**
**v0.1 implementuje LEN „shared database + shared schema" (row-level).** Dôvod: tenant izolácia v tomto modeli je *tá istá mašinéria ako row-level autorizácia* (§2.4 rule engine) — „ďalší povinný predikát pripojený ku každému dotazu". Nie je to samostatná veľká featura, je to špecializácia rule enginu, ktorý sa aj tak stavia → skoro zadarmo. Ostatné dva modely sa **neimplementujú**, ale **porty (tenant resolution, tenant-aware data access) sa navrhnú tak, aby boli neskôr len doplnením stratégie, nie prepisom.**

- **Shared database + shared schema (row-level, discriminator) — JEDINÝ model vo v0.1:** každá tabuľka má `tenant_id`, izolácia cez RLS / rule engine (§2.4). Najlacnejšie na prevádzku, najhustejšie; najvyššie riziko leaku pri chybe politiky (preto default-deny + adversarial suite). Pokrýva drvivú väčšinu SaaS.
- **Database-per-tenant — NESKÔR, pri reálnej potrebe (nie v0.1):** plná izolácia, per-tenant backup/restore, geo-rezidencia dát, „noisy neighbor" eliminovaný. Enterprise/regulované odvetvia. Iný mechanizmus (tenant resolution vyberie iný connection string namiesto predikátu) — reálna práca navyše. Pridá sa ako druhá stratégia do pripravenej abstrakcie + migrácia shared → DB-per-tenant (cenná escape hatch pre rastúceho tenanta).
- **Schema-per-tenant — VYHODENÉ / na neurčito:** Postgres schéma na tenanta. *Najslabší kompromis z troch* — katalógový bloat (degradácia vacuum/plánovača pri desiatkach tisíc schém), rozbíja transaction-mode connection pooling (per-request `search_path`), náklady blízko DB-per-tenant bez jeho izolácie. Neimplementovať, kým si to nevypýta konkrétny prípad — a aj vtedy zvážiť, či nie je lepší rovno DB-per-tenant.

Cieľ abstrakcie: **jednotná abstrakcia, jedna implementovaná stratégia (v0.1)**, s možnosťou neskôr pridať DB-per-tenant a *hybrid* (väčšina tenantov shared, VIP vlastná DB) bez zmeny aplikačného kódu. Presun tenanta medzi modelmi (malý narastie → vlastná DB) je akceptačné kritérium až pre fázu, kde DB-per-tenant pristane — nie v0.1.

> **Musí obsahovať — prierezovo**
- **Tenant resolution** — z čoho sa určí aktuálny tenant: subdoména, host header, JWT claim, API key, path segment. Pluggable stratégia; tenant kontext dostupný v celom requeste (ambient, cez DI scope).
- **Tenant-aware všetko:** auth (užívateľ patrí tenantovi, alebo je cross-tenant), storage (bucket/prefix per tenant), events & automation (§3 — pravidlá izolované per tenant), cache (§2.11 — prefix kľúča tenantom), telemetria (tenant ako dimenzia), rate limits & quoty (§2.12 — per tenant), messaging (§2.15).
- **Per-tenant konfigurácia** — feature flags, limity, branding, providery (tenant si prinesie vlastný SMTP/OAuth).
- **Tenant lifecycle** — provisioning (vytvorenie + migrácie + seed), suspend, export, delete (s istotou, že sa zmažú všetky jeho dáta naprieč všetkými komponentmi — súvisí s GDPR §5).
- **Cross-tenant operácie** — admin/platform úroveň, ktorá vidí naprieč tenantmi (billing, support), striktne oddelená a auditovaná.

> **Pozor na**
- **Tenant leak** — najhoršia možná chyba BaaS. Izolácia sa musí vynucovať na najnižšej vrstve (RLS / kompilovaný predikát pripojený ku každému dotazu vrátane realtime, cache, agregácií, automation), nie kontrolou v aplikačnom kóde, ktorú možno zabudnúť. Default-deny: dotaz bez tenant kontextu zlyhá, nie vráti všetko.
- **Connection pooling pri database-per-tenant** *(až keď tento model pristane, nie v0.1)* — tisíce tenantov = tisíce pripojení; potrebný pooling/multiplexing (PgBouncer/pgcat) a lazy connection management.
- **Migrácie naprieč N databázami** *(problém až pre DB-per-tenant, nie v0.1)* — DB-per-tenant znamená spustiť migráciu N-krát; orchestrácia, čiastočné zlyhania, verzia schémy per tenant.
- **Noisy neighbor** v shared modeli — jeden tenant vyčerpá zdroje; per-tenant rate limits a resource governance.
- **„Shared cache" leak** — cache/queue kľúče bez tenant prefixu prekrížia dáta (viď §2.11).
- **Data residency** — regulácia môže vyžadovať dáta tenanta v konkrétnom regióne → routing na regionálnu DB.

> **Akceptačné kritériá**
- Adversarial suite: request v kontexte tenanta A nikdy nevráti/nezapíše dáta tenanta B — cez REST, GraphQL, realtime, storage, cache, automation, agregácie.
- Dotaz bez tenant kontextu zlyhá (default-deny), nevráti dáta všetkých tenantov.
- *(Až pre fázu s DB-per-tenant, nie v0.1:)* presun tenanta shared → DB-per-tenant prebehne bez straty dát a bez zmeny aplikačného kódu. **Vo v0.1 platí slabšia forma:** abstrakcia je navrhnutá tak, že pridanie DB-per-tenant stratégie nevyžaduje zmenu aplikačného kódu ani portov.
- Delete tenanta odstráni 100 % jeho dát naprieč všetkými komponentmi (overené auditom).

> **.NET stavebné bloky**
**Finbuckle.MultiTenant** (MIT) — zrelá knižnica pre tenant resolution + per-tenant stores a EF Core integráciu. **Marten** má natívnu podporu troch multi-tenancy stratégií (conjoined / schema / database-per-tenant). Postgres RLS + session `tenant_id` pre row-level izoláciu. Pooling: PgBouncer / pgcat.

## §5 Audit, compliance & data governance

**Čo to je:** nemenný, dôveryhodný záznam „kto, kedy, čo urobil" + nástroje na plnenie regulácií (GDPR, SOC 2, HIPAA). V bežnej appke „nice to have", v BaaS pre enterprise/regulované odvetvia *vstupná podmienka*. Zároveň oblasť, kde má .NET s event sourcingom (Marten) prirodzenú výhodu — auditovateľná história je vedľajší produkt architektúry, nie dodatočná vrstva.

> **Musí obsahovať — Audit**
- **Append-only audit stream** oddelený od aplikačných dát a od observability logov (iný účel, iná retencia, iné oprávnenia na čítanie). Zaznamenáva: admin zásahy, service-role použitia, auth eventy, policy zmeny, prístup k citlivým dátam, exporty, mazania.
- **Obsah záznamu:** actor (kto — user/service/automation), akcia, cieľ (entita + id), pred/po hodnota (alebo diff), čas, tenant, request-id, IP/origin, výsledok.
- **Tamper-evidence** — hash chaining / append-only garancia, aby sa audit nedal spätne prepísať (regulačná požiadavka). Event sourcing to dáva zadarmo (immutable event log).
- **Napojenie na event systém (§3)** — audit je špecializovaný konzument toho istého event streamu, nie paralelná infraštruktúra.
- **Query & export auditu** pre compliance reporty; retencia konfigurovateľná (často roky) s presunom do lacného cold storage cez `IObjectStore`.

> **Musí obsahovať — Compliance & governance**
- **GDPR data subject requests:** *right to access / portability* (export všetkých dát užívateľa v strojovom formáte naprieč všetkými komponentmi — DB, storage, logy) a *right to erasure* (úplné zmazanie vrátane záloh a odvodených dát; alebo dokumentovaná anonymizácia).
- **PII klasifikácia** — označenie citlivých polí v schéme (metadáta), aby ich systém vedel automaticky vylúčiť z logov, cache a webhook payloadov a zahrnúť do erasure.
- **Data retention policies** — automatické mazanie/anonymizácia po uplynutí doby (napojené na scheduled automation §3).
- **Consent tracking** — kde je relevantné (messaging opt-in §2.15).
- **Encryption** — at-rest (DB, storage) a in-transit (TLS) ako baseline; field-level encryption pre najcitlivejšie polia (kľúče cez `ISecretStore` §1).
- **SOC 2 / ISO podporné artefakty** — audit trail, access logs, change management cez migrácie v Git; framework má poskytnúť „materiál" pre auditora.

> **Pozor na**
- **Right to erasure vs append-only audit** — priamy konflikt: audit nesmieš prepísať, ale PII musíš zmazať. Riešenie: crypto-shredding (zmaž šifrovací kľúč → dáta sú nečitateľné, audit záznam ostane), alebo pseudonymizácia (audit odkazuje na subject id, nie na PII).
- **Erasure naprieč zálohami** — dáta žijú aj v backupoch (§7) a v odvodených miestach (cache, search index, webhook logy, data warehouse); erasure musí byť koordinovaná naprieč všetkými, inak je právne neúplná.
- **Audit ako výkonnostná záťaž** — nezapisuj synchronne do hot path; async cez outbox (§3).
- **Kto smie čítať audit** — audit obsahuje citlivé informácie; prístupovo oddelený, sám auditovaný.
- **Multi-tenant audit** — izolovaný per tenant, ale platform-level audit vidí naprieč (a je oddelený).

> **Akceptačné kritériá**
- Každá admin/service-role operácia vytvorí audit záznam s actor, cieľom, diff, časom a tenantom.
- Pokus o spätnú modifikáciu audit záznamu je detekovateľný (hash chain break).
- GDPR export vráti kompletné dáta užívateľa naprieč DB, storage aj logmi v strojovom formáte.
- Erasure request odstráni/znečitateľní PII vrátane záloh a odvodených miest; audit integrita zostane zachovaná (crypto-shredding).

> **.NET stavebné bloky**
**Marten event sourcing** (immutable log = audit zadarmo + tamper-evidence). Field-level encryption cez `ISecretStore` (Key Vault/Vault). PII klasifikácia ako atribúty/metadáta schémy, vynucovaná v serializácii, logovaní a cache kľúčoch.

## §6 Machine-to-machine auth & OpenAPI pre integrácie

**Čo to je:** prístup k backendu nie z UI koncového užívateľa, ale zo *servera* tretej strany, CI pipeline, partnerského systému alebo skriptu. Iný auth model (žiadny interaktívny login), iné tokeny, iné scopes — a publikované API + SDK, aby sa integrátor vôbec pripojil. V BaaS býva odbité „service key" (Supabase `service_role`), čo je pre reálne integrácie hrubé (jeden all-powerful kľúč). Toto je oblasť, kde .NET framework s prvotriednym OpenIddictom a OpenAPI môže výrazne prekonať konkurenciu.

> **Musí obsahovať — M2M auth**
> **Rozsah v0.1 — začni PAT, OAuth client credentials neskôr**
**v0.1 implementuje LEN Personal Access Tokens (PAT).** Pokrýva väčšinu skorých integrácií (skripty, CI, jednoduché server-to-server) a je dramaticky jednoduchší — scoped API key s expiráciou, revokáciou a „last used", žiadny OAuth autorizačný server. **OAuth 2.1 client credentials flow sa neimplementuje**, ale token-issuance/validation port a scope model sa navrhnú tak, aby client credentials bol neskôr *doplnenie spôsobu získania scoped tokenu*, nie prepis. **Tvrdá podmienka aj pre PAT-only: scopes od prvého dňa** — PAT bez scopes je len „all-powerful service_role kľúč" pod iným menom, presne ten anti-pattern, pred ktorým táto sekcia varuje.

- **Personal Access Tokens (PAT) — JEDINÝ mechanizmus vo v0.1:** dlhožijúci token viazaný na užívateľa/service účet, **so scopes** a expiráciou, revokovateľný, s „last used". Pre skripty, CI, jednoduché integrácie (štandard GitHub/GitLab).
- **Scopes / least privilege — povinné aj pri PAT:** token nesie presne to, čo smie (`orders:read`, `invoices:write`), nie „všetko". Per-integration granularita. Toto je zdieľaná mašinéria, na ktorú OAuth client credentials neskôr len nasadne.
- **OAuth 2.1 client credentials flow — NESKÔR, nie v0.1:** štandardný M2M grant (`client_id` + `client_secret`, lepšie *private_key_jwt* / mTLS → scoped access token, krátkožijúci). Pridá sa ako druhý spôsob získania scoped tokenu do pripravenej abstrakcie, keď reálne integrácie prerastú PAT.
- **Dva svety tokenov jasne oddelené** (platí od v0.1): user tokeny (OIDC, krátke, refresh) vs integračné tokeny (PAT, scoped) — a service-role bypass (§2.2) len pre dôveryhodný server-side kód, auditovaný.
- **Rotácia a revokácia bez výpadku** (prekryv dvoch platných secretov), rate limits a quoty per integrácia (§2.12).
- **Webhook autentifikácia oboma smermi** — outbound podpisovanie (§3.4) aj inbound overenie (integrácia volá náš HTTP endpoint / funkciu — §2.7 — s tokenom alebo podpisom).

> **Musí obsahovať — OpenAPI pre integrácie**
> **Rozsah v0.1 — publikovaný OpenAPI + docs; SDK/portál/verzovanie neskôr**
**v0.1: publikovaný OpenAPI 3.1 dokument + interaktívne docs (Scalar).** Oboje je skoro zadarmo — OpenAPI sa generuje zo schémy aj tak a Scalar je pár riadkov (autor má priamu skúsenosť z MMLib.OpenApiForYarp). **Kľúčové zdôvodnenie škrtov:** keď je publikovaný dobrý OpenAPI, integrátor si *vygeneruje vlastného klienta sám* v akomkoľvek jazyku (Kiota/NSwag/openapi-generator) — first-party SDK generovanie je pohodlie navyše, nie predpoklad integrácie. SDK gen, developer portál a API verzovanie sa pridajú, až keď má framework reálnych externých integrátorov. **Dedikovaný sandbox/test prostredie je mimo rozsahu úplne** — Alvo je self-hostovateľné a vytvoriť testovací projekt (standalone Docker / samostatný projekt v inštancii) je triviálne, takže integrátor si vlastný sandbox spraví sám; first-party sandbox by len duplikoval branching (§2.13), čo je ťažká featura možno-nikdy.

- **Publikovaný, verzovaný OpenAPI 3.1 dokument — v0.1:** generovaný zo schémy (§2.1) — zahŕňa CRUD, custom endpointy, auth schémy, chybové modely (RFC 7807). Contract test, že zodpovedá reálnemu správaniu (viď „pozor na" OpenAPI drift).
- **Interaktívna dokumentácia (Scalar) — v0.1:** „try it" + auth flow. Developer portál s API kľúčmi a usage je *neskôr* (heavier).
- **SDK generovanie — NESKÔR (nie v0.1):** z OpenAPI → klient v jazyku integrátora; pre .NET aj first-party typovaný klient (`MMLib.Alvo.Client`). Do v0.1 netreba — integrátor si klienta vygeneruje sám z publikovaného OpenAPI.
- **API verzovanie a deprecation policy — NESKÔR (keď sú externí integrátori):** integrácie tretích strán nesmieš rozbiť zmenou schémy; verzie, sunset hlavičky, deprecation okno. Disciplína, ktorú treba mať v hlave už teraz, ale mechanizmus až keď je koho nerozbiť.

> **Čo je štandard**
OAuth 2.1 client credentials + JWT access tokens s scopes je štandard M2M (rovnaký model ako Stripe, GitHub Apps). PAT je štandard pre CI/skripty (GitHub, GitLab). OpenAPI 3.1 + generované SDK + developer portál je štandard API-first firiem. Standard Webhooks (§3.4) pre podpisovanie.

> **Pozor na**
- **Zdieľaný all-powerful kľúč** (Supabase service_role štýl) — pohodlný, ale pri úniku katastrofa a bez granularity. Preferuj scoped client credentials.
- **Secret v klientovi** — client credentials patria len na server; nikdy do SPA/mobilu (tam OIDC user flow).
- **OpenAPI drift** — generovaný dokument musí zodpovedať reálnemu správaniu (contract testy), inak integrátori stavajú na fikcii.
- **Breaking changes** pre externých konzumentov — najbolestivejšie; verzovanie a komunikácia sú povinné.
- **Token leakage v logoch / trace** (§2.12 redaction).

> **Akceptačné kritériá**
- Integrácia získa scoped token cez client credentials a dostane sa *len* k povoleným zdrojom (403 mimo scope).
- Revokácia integračného kľúča zablokuje prístup do 1 s; rotácia prebehne bez výpadku.
- Publikovaný OpenAPI validuje a zodpovedá reálnemu API (contract test); vygenerovaný klient funguje end-to-end.
- Zmena schémy nerozbije integráciu na predchádzajúcej verzii API počas deprecation okna.

> **.NET stavebné bloky**
**PAT (v0.1)** nepotrebuje OAuth server — stačí vlastná scoped-token tabuľka (hash tokenu, scopes, expirácia, revokácia, last-used) + validačný middleware. **OpenIddict** (Apache 2.0) sa pridá až s OAuth 2.1 client credentials (scopes, introspection, token revocation; **nie Duende**) — nie vo v0.1. **Microsoft.AspNetCore.OpenApi** + **Scalar** pre OpenAPI 3.1 a interaktívne docs (tu má autor priamu skúsenosť z MMLib.OpenApiForYarp). Kiota / NSwag pre SDK generovanie (tiež neskôr).

## §7 Platform services (secrets, backup/PITR, jobs)

Menšie, ale nevynechateľné prevádzkové komponenty. Všetky sú *swappable providery* (§1) — príklad, prečo je provider model prierezový princíp, nie akademická abstrakcia.

### 7.1 Secrets management `ISecretStore`

**Čo to je:** bezpečné uloženie tajomstiev — provider credentials (SMTP, OAuth, SMS), signing keys (JWT, webhooky), field-level encryption keys, per-tenant secrets. **Musí obsahovať:** centrálny store za rozhraním `ISecretStore` s implementáciami Azure Key Vault / HashiCorp Vault / K8s Secrets / šifrovaný súbor (dev); rotáciu kľúčov (najmä JWT signing — key overlap počas rotácie); versioning secretov; audit prístupu (§5). **Pozor na:** bootstrap paradox (credentials na secret store cez managed/workload identity, nie ďalší secret — §1); secrets nikdy do logov/env dumpov/git; per-tenant izolácia secretov. **Akceptačné kritériá:** rotácia signing key bez invalidácie platných tokenov; prepnutie providera bez zmeny kódu; žiadny secret v telemetrii.

### 7.2 Backup, restore & disaster recovery `IBackupTarget`

**Čo to je:** ochrana pred stratou dát a schopnosť obnovy. Pre self-host je to zodpovednosť frameworku, nie „necháme na užívateľa". **Musí obsahovať:** automatické zálohy (DB + objektové úložisko + konfigurácia/secrets metadata) na `IBackupTarget` (Blob / S3 / lokálny disk); **Point-in-Time Recovery (PITR)** cez WAL archiváciu — obnova na konkrétnu sekundu, nie len na nočný snapshot; definované **RPO/RTO** (koľko dát/času môžeš stratiť); pravidelný *test obnovy* (netestovaná záloha = žiadna záloha); per-tenant restore pri DB-per-tenant modeli (§4); verzované, šifrované zálohy s retenciou. **Štandard:** pgBackRest / Barman / WAL-G pre Postgres PITR; Supabase/Neon poskytujú PITR ako feature. **Pozor na:** konzistencia DB + storage snapshotu (objekty a metadáta musia sedieť — §2.6); zálohy v inom regióne/účte (ransomware, zmazaný účet); GDPR erasure musí zasiahnuť aj zálohy (§5); šifrovanie záloh a bezpečné uloženie kľúčov. **Akceptačné kritériá:** obnova na ľubovoľný bod v retenčnom okne v teste; automatizovaný restore drill v CI/staging; RPO/RTO merateľné a splnené.

### 7.3 Scheduled jobs & background processing

**Čo to je:** plánované a dlhobežiace úlohy — cron (denné reporty, cleanup, retention §5), delayed jobs (pripomienka o 24 h), recurring maintenance, dávkové spracovanie. Prekrýva sa s automation triggermi (§3), ale zaslúži explicitné pokrytie. **Musí obsahovať:** cron scheduling (ekvivalent `pg_cron`), one-shot delayed execution, durable jobs (prežijú reštart), retries + DLQ, distribuovaný scheduler (v multi-instance nasadení sa job spustí *raz*, nie N-krát — leader election / distributed lock), per-tenant izolácia jobov, viditeľnosť v admin UI (§2.14 studio). **Pozor na:** double-execution v distribuovanom prostredí (distributed lock); long-running job blokujúci worker pool (oddelené fronty); časové pásma a DST v cron výrazoch; idempotencia (job môže bežať viackrát pri retry). **Akceptačné kritériá:** cron job sa v 3-instance nasadení spustí práve raz; delayed job prežije reštart; zlyhaný job ide do DLQ s dôvodom. **.NET bloky:** **Hangfire** (LGPL, persistentné joby + dashboard) alebo **Quartz.NET** (Apache) pre cron; **Wolverine** scheduled messages; distributed lock cez Postgres advisory locks / Redis.

## §8 SQL vs NoSQL vs hybrid

### 8.1 SQL vs NoSQL — jednoznačná voľba

**Prečo Firebase zvolil dokumentový model:** optimalizácia pre mobilný realtime sync a schema-less prototypovanie. Dôsledky: nutná *denormalizácia*, *komplexné Security Rules* (autorizácia naprieč denormalizovanými kópiami je bug-prone), zato *jednoduchší offline sync* — dokument je prirodzená jednotka synchronizácie a konfliktu.

**Prečo Supabase / Nhost / PocketBase išli relačne:** SQL sila (joins, transakcie, constraints), zrelý ekosystém (PostGIS, pgvector, extensions), RLS ako dokázateľná autorizácia, nízky lock-in (`pg_dump` a odídeš). Cena: nutnosť definovať schému.

**Convex — hybrid:** dokumenty + relačné vzťahy + reactive queries v TypeScripte; obetuje SQL za reaktivitu a end-to-end type safety — roky vývoja proprietárneho reactive runtime, mimo rozsah wedge produktu (§13.4).

> **Google sám priznal hodnotu SQL**
Firebase Data Connect (nad Cloud SQL PostgreSQL) sa v apríli 2026 premenoval na **Firebase SQL Connect** — pridal Realtime PostgreSQL, natívny raw SQL a custom resolvers. Aj NoSQL-first hráč potrebuje relačný model pre komplexné aplikácie.

> **Rozhodnutie: relačný model, bodka**
Bez ohľadu na konkrétny engine (§8.2) je dátový model **relačný, nie dokumentový**. Dôvody sú nezávislé od voľby Postgres/SQL Server/SQLite: cieľová skupina (.NET vývojári) myslí relačne a pozná EF Core/Dapper/SQL — dokumentový model by zahodil presne tú znalosť, ktorú framework chce využiť; joins, constraints a transakcie sú pre biznis aplikácie (faktúry, objednávky, multi-tenant SaaS — §4) prakticky nenahraditeľné bez neustálej denormalizačnej gymnastiky; a SQL je v tréningových dátach coding agentov (§9) extrémne zastúpený — vlastný dokumentový query jazyk by bol pre agentov cudzí jazyk navyše.

### 8.2 Ktorý SQL engine — dev-mode SQLite, produkcia Postgres *aj* Azure SQL

Pôvodný litmusový test Supabase znel: *„Dokáže užívateľ tento produkt spustiť s ničím iným než PostgreSQL databázou?"* Pre embedded .NET framework treba latku posunúť ešte ďalej: **dokáže si to užívateľ stiahnuť a spustiť s ničím iným než tým, čo má na disku?** To znamená `dotnet add package` / `dotnet run` → embedded **SQLite** súbor → funkčný backend za pár sekúnd, bez Dockera, bez cloud účtu — presne DX, ktorý dokazuje PocketBase (jeden Go binary + SQLite, 59K★). Produkcia potom cieli na plnohodnotný server engine — a keďže cieľová skupina sú .NET tímy, nemôže to byť len Postgres: veľká časť z nich je **SQL Server / Azure SQL shop**, nie Postgres shop, a odmietnutie tejto voľby by rovno vyradilo časť trhu, o ktorý sa framework uchádza (§12.1).

| Režim | Engine | Účel |
|---|---|---|
| **Dev / try-it** | SQLite | Zero-friction vyskúšanie — stiahni, spusti, funguje. Žiadna inštalácia, žiadna závislosť. |
| **Produkcia** | PostgreSQL | Predvolené odporúčanie — najviac natívnych schopností (pgvector, JSONB, logical replication ako voliteľný hardening). |
| **Produkcia** | Azure SQL / SQL Server | Pre .NET/enterprise tímy, ktoré v tomto ekosystéme už žijú — časť cieľovej skupiny, ktorú by Postgres-only voľba vyradila. |

> **Prečo to tentokrát nie je „least common denominator" past**
V predchádzajúcich verziách tejto analýzy stálo varovanie: nedávaj RLS, WAL a autorizáciu za DB abstrakciu, lebo skončíš na najslabšom spoločnom menovateli. To varovanie platí naďalej — ale kľúčový postreh je, že **RLS a WAL neboli fundamentálne nutné pre embedded model, boli nutné len pre architektúru typu Supabase**, kde REST vrstva, dashboard aj priame SQL pristupujú k tej istej databáze viacerými nezávislými cestami naraz — enforcement musí sedieť tam, kde sa tieto cesty stretávajú, teda v databáze samotnej.

Embedded .NET framework túto vlastnosť nemá: **jediná cesta k dátam je cez framework.** Keď je to pravda, aplikačne vynucované pravidlo — presne to, čo je navrhnuté v §2.4 (kompilácia rule do parametrizovaného SQL predikátu) — je rovnako bezpečné ako natívne RLS, lebo neexistuje spôsob, ako ho obísť. To isté pre realtime: namiesto čítania WAL zvonka môže framework publikovať event **priamo v tej istej transakcii, v tom istom procese** (§2.5, §3.2 transactional outbox) — nižšia latencia, žiadny WAL, funguje identicky na Postgrese, SQL Serveri aj SQLite. Jadro (rule engine, event systém, multi-tenancy) je teda od návrhu DB-agnostické — abstrahuje sa *úložisko a jeho natívne bonusy*, nie autorizácia a realtime, presne rozlíšenie z pôvodného pravidla. **Poctivá výhrada:** premisa „jediná cesta k dátam je cez framework" sa dostáva do konfliktu s escape hatch filozofiou (§10.3 — priame SQL, zdieľanie DB s inými službami); riešenie tohto napätia (RLS defense-in-depth na Postgrese, CDC hardening, explicitný trust model) je rozobrané v §10.3.

| Schopnosť | PostgreSQL | Azure SQL / SQL Server | SQLite (dev) | Dopad na jadro |
|---|---|---|---|---|
| Rule engine, event systém, multi-tenancy | app-side | app-side (identické) | app-side (identické) | **žiadny** — jadro nezávislé od enginu |
| Vector search | pgvector | natívny `VECTOR` typ (2025+) | sqlite-vec (nezrelé) | menší, než pred rokom — obe produkčné majú natívnu podporu; dev-mode degraduje |
| JSON stĺpce | JSONB (binárne, indexovateľné) | JSON (textové) | JSON1 (textové) | reálna, ale zvládnuteľná strata mimo Postgres |
| Zmeny dát pre CDC/audit hardening | WAL / logical replication | Change Tracking / CDC / temporal tables | žiadne (polling) | iný mechanizmus, porovnateľný výsledok — jadro (§2.5, §3) na tom nestojí |
| Concurrent writes | MVCC | MVCC (Snapshot Isolation) | **single-writer** (aj vo WAL móde) | tvrdý strop len na SQLite — v poriadku pre dev/solo, nie pre produkčný multi-tenant SaaS |

> **Pozor na**
- **SQLite single-writer je skutočný, tvrdý strop** — nie kompromis na vyladenie, ale architektonická hranica. Framework si to interne odseriuje (write-queue, presne ako PocketBase), čo je v poriadku pre dev/solo/prototyp, ale musí byť *explicitne zdokumentované*, že SQLite nie je produkčná cesta pre viac než triviálnu súbežnosť zápisov.
- **EF Core migrácie cross-provider nie sú zadarmo** — fungujú dobre naprieč SQLite/PostgreSQL/SQL Server, ale generovaný SQL sa líši (typy, defaulty, obmedzenia). Nutné testovať CI proti všetkým trom, nie len proti jednému a predpokladať zvyšok.
- **Rozdielne izolačné úrovne** — SQL Server default (Read Committed so snapshotom cez RCSI) sa správa inak než Postgres default; môže to ovplyvniť správanie rule enginu a event systému pri súbežných zápisoch. Kontraktné testy musia pokryť rovnaké scenáre na oboch.
- **Migračná cesta dev → produkcia** nesmie byť len „dobrá rada" — potrebuje nástroj (export zo SQLite, replay migrácií na cieľovom engine, prípadne dátový import), inak je zero-friction dev zážitok slepá ulička pri prechode do produkcie.
- **Vector search v dev-mode** — sqlite-vec je nezrelý; ak appka počas vývoja spolieha na sémantické vyhľadávanie, dev-mode ho buď nebude mať plnohodnotne, alebo bude vyžadovať Postgres/Azure SQL už vo vývoji (výnimka z „len SQLite" pravidla, ktorú treba priznať, nie skrývať).

> **Akceptačné kritériá**
- `dotnet run` na čerstvom stroji bez akejkoľvek DB inštalácie vytvorí funkčný backend so SQLite do niekoľkých sekúnd.
- Rovnaký adversarial test suite (rule engine, multi-tenancy izolácia — §4) prejde identicky na SQLite, PostgreSQL aj Azure SQL (contract tests per engine).
- Migrácia projektu z dev SQLite na produkčný Postgres/Azure SQL má zdokumentovaný a otestovaný nástroj/postup, nie len manuálny návod.
- Concurrent write benchmark na SQLite dáva konkrétne, publikované číslo (zápisov/s), pri ktorom framework odporúča prechod na server engine — nie vágne „pre produkciu nepoužívajte".

> **Event sourcing pre .NET**
**Marten** (dokumenty + event store nad Postgres JSONB) umožňuje auditovateľnú históriu zmien ako first-class feature (§5) — je to voliteľný hardening na Postgres produkčnej ceste, nie súčasť jadra, ktorá by blokovala SQL Server alebo SQLite.

## §9 AI-readiness — dizajn pre éru coding agentov

Coding agenti (Claude Code, Cursor, Copilot) sa stávajú primárnym „užívateľom" backendu. BaaS, ktorý je agent-first, vyhrá — Supabase to explicitne pripisuje svojmu rastu („vibe-coding" naratív Série F).

### 9.1 MCP servery konkurencie

MCP (Model Context Protocol) je pre Alvo **voliteľný adaptér nad Management API**, nie konfiguračná cesta: konfigurácia backendu stojí na descriptore (agent vygeneruje súbor) a runtime správa na Management API. MCP dáva zmysel len pre scenár „ovládaj bežiacu inštanciu z externého agentického nástroja". Nižšie je prehľad, ako to riešia iní.

- **Supabase MCP** (OAuth 2.1, dynamic client registration) — pokrýva celý backend: `list_tables`, `execute_sql`, `apply_migration`, `deploy_edge_function`, `generate_typescript_types`, `get_logs`, `get_advisors`, `search_docs`; distribuovaný aj ako plugin (MCP + skills). Lokálne na `localhost:54321/mcp`.
- **Neon MCP** — branch management a safe migrations; copy-on-write branching s dátami je pre agentov testujúcich migrácie ideálny.
- **Convex MCP** + evals — Convex meria agent-úspešnosť benchmarkami (`convex-evals`, „LLM Leaderboard") a argumentuje: *„AI can generate database code using the large training set of TypeScript code without switching to SQL."*
- **Data API builder** má MCP od v1.7+ (`dml-tools` per entita) — .NET základ zadarmo.

### 9.2 Agent-first checklist pre nový framework

#### Jeden formát, dva zdroje pravdy

Schéma má jeden kanonický *tvar* — descriptor (§2.14) — ale môže *žiť* na dvoch miestach: ako **súbor v repe** (GitOps: zdroj pravdy je Git, agent číta/píše súbory, UI je editor tých istých súborov) alebo ako **záznam v DB bežiacej inštancie** (dashboard-first: klik/agent zapíše zmenu do schema registry, žiadny súbor). Most medzi svetmi je obojsmerný **export/import** descriptora — z bežiacej inštancie ho vyexportuješ do Gitu a naopak. Tvrdenie „všetko sú súbory" platí pre GitOps cestu, nie pre dashboard-first.

#### Bezpečnosť podľa cesty

**GitOps/embedded:** C# codegen zo schémy + Roslyn analyzery = chyba agenta padne *pri builde*, nie v produkcii (štrukturálna výhoda oproti TS aj SQL). **Dashboard-first** nemá build — entita vzniká za behu — takže tu istotu drží **runtime validácia descriptora proti JSON Schéme + kontrola pri apply** (fail-fast, diff pred aplikovaním). Compile-time istota je teda bonus GitOps cesty, nie univerzálna vlastnosť.

#### Descriptor + Management API ako jadro

Agent stavia backend tak, že generuje **descriptor** proti JSON Schéme a aplikuje ho (Docker/CLI/API) — žiadny protokol navyše. **MCP je voliteľný adaptér** nad Management API pre externých agentov (Claude Code nad bežiacou inštanciou): parita so Supabase MCP (schéma, migrácie, dáta, logy, codegen), ale *nice-to-have*, nie stavebný kameň.

#### Štruktúrované chyby

Agent sa učí z chýb. RFC 7807 s kódom, príčinou a *návrhom opravy* („stĺpec X neexistuje; podobné: Y") — podceňovaný diferenciátor.

#### Determinizmus a idempotencia

Migrácie a operácie bezpečne opakovateľné; deklaratívna schéma + diff namiesto sekvencie ALTERov.

#### Jeden príkaz = celý stack

`supabase start` ekvivalent: CLI spustí Postgres + auth + storage + realtime + studio lokálne; `llms.txt` + strojová dokumentácia.

## §10 Extensibility a escape hatch

### 10.1 Spektrum custom logiky koncového užívateľa

Od najjednoduchšieho po najkomplexnejšie — dobrý BaaS pokrýva celé spektrum, aby užívateľ neopustil platformu pri prvej neštandardnej požiadavke:

- **Computed fields** (výraz v expression language),
- **validation hooks** (blocking before-hook s podmienkou),
- **automation rules** (§3 — deklaratívne, bez kódu),
- **csx skripty** (standalone aj embedded — custom C# logika bez build pipeline, trust: admin kód, §2.6),
- **hooks v C#** (BeforeCreate/AfterUpdate delegáty),
- **custom endpointy v C#** (plný ASP.NET Core s BaaS kontextom — dáta s policy vynucovaním, identita, eventy),
- **pluginy** (AssemblyLoadContext balíčky: auth providery, storage drivery, action typy do §3 katalógu).

### 10.2 Framework-level modely konkurencie

**PocketBase**: dve cesty — Go extending (vlastný binary, 70+ hookov) alebo JSVM (goja, synchronný, bez Node modulov). **Parse**: Cloud Code. **Appwrite**: Functions v 15+ runtimoch. .NET výhoda: *všetkých šesť úrovní vyššie je ten istý jazyk a runtime*.

### 10.3 Escape hatch

> **Parse shutdown — kanonická lock-in lekcia**
Facebook oznámil vypnutie Parse v januári 2016 (ročné okno, ~600 000 aplikácií); open-sourcnutý Parse Server bol forknutý 3 000+ krát. Lekcia: pod akoukoľvek akvizíciou či vypnutím musí mať užívateľ plnú kontrolu — open-source jadro, štandardný Postgres, žiadny proprietárny formát.

**Firebase lock-in:** migrácia = prepis data vrstvy (dokumenty → tabuľky, Security Rules → RLS) + pricing šoky per-operation billingu. **Supabase argument:** „it's just Postgres" — `pg_dump` a odídeš.

> **.NET gradient**
Keďže host aj custom kód je C#/ASP.NET Core, escape hatch je plynulý: auto API → rules → hooks → custom endpointy → plný ASP.NET Core. „Prerastenie" frameworku neznamená opustenie runtime — štrukturálna výhoda, ktorú JS/Go platformy nemajú.

> **Escape hatch vs bezpečnostný model — priznaný konflikt**
Argument z §8.2 („aplikačné pravidlá sú rovnako bezpečné ako RLS, lebo jediná cesta k dátam je cez framework") platí *len dovtedy, kým je to pravda*. Escape hatch tejto sekcie — priame SQL, reporty tretej služby nad tou istou DB, `pg_dump` mentalita — tú premisu vedome porušuje. Dôsledky treba pomenovať natvrdo: **out-of-band zápis obchádza rule engine, neemituje eventy** (realtime klienti nedostanú update, automation rules sa nespustia, audit má dieru). Mitigácia: (1) na Postgrese zapnúť natívne RLS ako defense-in-depth *navyše* k aplikačnej vrstve — vtedy ani priame SQL neobíde izoláciu; (2) voliteľný `IChangeFeed` hardening (WAL / Change Tracking) doplní eventy za out-of-band zmeny; (3) dokumentácia musí explicitne definovať, ktoré garancie platia len pri prístupe cez framework. Toto nie je detail — je to trust model celého produktu.

### 10.4 Knižnica descriptorov (predpripravené templaty)

V IT systémoch sa stále dokola modeluje to isté: tagy, adresa, kontakt, číselníky (krajiny, meny), audit stĺpce, mäkké mazanie. Zamýšľaný smer (nie v0.1): **kurátorská knižnica znovupoužiteľných descriptorov**, ktoré si vývojár pripne k projektu — nie ako modul v jadre (to by bol druhý zdroj pravdy, §9.2, a slippery slope k opinionated app platforme), ale ako **descriptor fragment, ktorý ostáva v descriptorovom formáte** (verzovateľný, agent-friendly, deklaratívny).

> **Copy vs reference — rozhodnutie, ktoré určuje zložitosť**
Dva principiálne modely spojenia templatu s projektom: **(A) Copy/scaffold** — template sa pri pridaní skopíruje do descriptora, odteraz je tvoj (jednoduché, žiadna runtime väzba ani verzie; nevýhoda: žiadne aktualizácie, fork navždy). **(B) Reference/import** — descriptor sa odkazuje na samostatný template a ťahá ho za behu (DRY, aktualizácie; ale vzniká *dependency systém pre descriptory* — verzovanie, rozlišovanie mien, kompozícia, resolver — čo je notoricky ťažký problém à la npm/NuGet). **Odporúčanie:** začať modelom A (lacný, 80 % hodnoty, nezavrie dvere k B, lebo formát fragmentu je ten istý); model B až keď sa dokáže potreba zdieľať a aktualizovať naprieč projektmi. Ten istý princíp ako „~10 balíkov nie 35" a „dva zdroje pravdy": konzervatívny štart, drahý dependency systém až s dôkazom.

Nezávisle od A/B treba dotiahnuť: **namespacing** (template `address` nesmie kolidovať s tvojím — konvencia `std_*` alebo namespace), **čo template vlastní** (pre v0.1 obmedziť na entity + polia + vzťahy; rules/hooky sú príliš kontextové), a **kurátorstvo** (hŕstka oficiálnych templatov, zvyšok komunita). Patrí do samostatného projektu/balíka, nie do jadra — F7+, kandidát na neskoršiu špecifikáciu.

## §11 Konkurenčná matica

| Platforma | GitHub ★ | Implementácia | Licencia | Vznik | Financovanie |
|---|---|---|---|---|---|
| **Supabase** | ~100K | Postgres + PostgREST (Haskell), GoTrue (Go), Realtime (Elixir) | `Apache 2.0` | 2020 | >$1 mld.; $500M Séria F @ $10,5 mld. (jún 2026, GIC) |
| **Firebase** | — | Google Cloud | `proprietárny` | 2011 (Google 2014) | Google |
| **Appwrite** | ~56,5K | TypeScript + PHP/Swoole | `BSD-3` | 2019 | ~$37M |
| **PocketBase** | ~59K | Go + SQLite | `MIT` | 2022 | žiadne (solo, v0.x) |
| **Convex** | ~12K | Rust + TypeScript | `FSL` (→ Apache po 2 r.) | 2021 (OSS 2024) | ~$26M (a16z) |
| **Nhost** | ~8K | Hasura + Postgres | `MIT` | 2019/20 | $3M seed |
| **Parse Server** | ~21K | Node.js + MongoDB | `Apache 2.0` | 2016 (OSS) | komunitný |
| **Directus** | ~36K | TypeScript / Vue | `MSCL-1.0` | ~2020s | ~$8,5M |
| **InstantDB** | tisíce | Clojure + TS SDKs | open-source | 2022 (YC) | $3,4M seed |
| **Triplit** | — | TypeScript | open-source | 2020 (YC) | akvírovaný Supabase (okt 2025) |
| **ElectricSQL** | ~8–9K | Elixir + TS | `Apache 2.0` | 2021 | seed |
| **Amplify (AWS)** | ~9,5K | TS + multi SDKs | `Apache 2.0` | 2017 | AWS |

Supabase Séria F: $500M, vedená GIC, $10,5 mld. post-money, ~10M vývojárov. Akvizícia Triplitu (okt 2025): spoluzakladateľ vedie v Supabase third-party integrácie (ElectricSQL, Zero, PowerSync) — riešenie offline-first slabiny.

## §12 Prieskum príležitostí (market gaps)

### 12.1 .NET-native BaaS — neexistuje (overené)

> **Hlavná príležitosť**
Parse a Appwrite majú .NET len ako klientske SDK. Enterprise .NET tímy dnes buď používajú TypeScript/Go platformy (kultúrny a prevádzkový mismatch), alebo stavajú všetko ručne.

### 12.2 Multi-tenancy first-class

SaaS staviteľia to riešia ručne cez RLS gymnastiku; žiadny mainstream BaaS nemá tenant izoláciu ako built-in koncept (JasperFx uvádza multi-tenancy medzi najčastejšími konzultačnými dopytmi).

### 12.3 Enterprise-grade automation (§3)

Ako ukázala tabuľka §3.1 — Supabase má surové triggery, Appwrite nevie podmienky na zmenu atribútu, Directus nie je OSS a per-item behy neškálujú. *Deklaratívny ECA engine s outboxom a Standard Webhooks doručovaním* je otvorená pozícia.

### 12.4 Offline-first / local-first sync

ElectricSQL, PowerSync, InstantDB, Triplit, Zero — silný trend; Supabase preto kúpil Triplit. Medzera: žiadny .NET-native sync engine (MAUI/WPF appky).

### 12.5 Embedded BaaS ako knižnica

PocketBase dokazuje dopyt po embeddable backende, ale je Go/SQLite. NuGet knižnica do existujúcej ASP.NET Core appky by bola unikátna.

### 12.6 Durable workflows, agent-first, compliance

Convex má durable execution; InsForge cieli agentov; audit/on-prem segment je JS/Go platformami pokrytý slabo — .NET s Marten event sourcingom a Entra ID integráciou tam má prirodzenú kredibilitu.

## §13 Odporúčania pre návrh — syntéza

### 13.1 Čo prevziať od koho

| Prvok | Od koho | Prečo |
|---|---|---|
| „It's just Postgres" — nízky lock-in | Supabase | Escape hatch cez `pg_dump`; RLS, pgvector, WAL zadarmo |
| Per-operation rules, čitateľné výrazy | PocketBase | Najjednoduchší autorizačný model — s kompiláciou do SQL |
| Unified event katalóg s wildcardmi | Appwrite | Jeden event systém poháňa realtime + webhooky + funkcie |
| ECA flows: trigger–condition–actions, blocking vs non-blocking | Directus | Najprepracovanejší automation model — opraviť jeho diery (condition pred behom, batch coalescing) |
| Event-triggered functions | Firebase | Definičná vlastnosť kategórie |
| Deklaratívny config + MCP | Data API builder | Agent-first, .NET-native, MIT |
| Embeddable jednoduchosť | PocketBase | NuGet namiesto microservices |
| Branching s dátami | Neon | Migrácie testované na realistických dátach |
| Webhook delivery kvalita | Stripe / Standard Webhooks | Signing, retries, DLQ, redelivery — latka mimo BaaS kategórie |

### 13.2 Odporúčaný stack (permisívne licencie)

| Vrstva | Blok | Licencia | Poznámka |
|---|---|---|---|
| Databáza (dev) | SQLite | `Public Domain` | Zero-friction — `dotnet run` a ide |
| Databáza (produkcia) | PostgreSQL *alebo* Azure SQL / SQL Server | `OSS / komerčná (Azure)` | Jadro (rule engine, eventy, tenancy) je app-side → engine-agnostické (§8.2) |
| Driver + CDC | Npgsql (logical replication) | `PostgreSQL` | WAL ako `IAsyncEnumerable` |
| Data API | DAB vzory / vlastný generátor | `MIT` | + MCP server |
| GraphQL | HotChocolate | `MIT` | Fáza 2+ |
| Auth / OIDC | ASP.NET Core Identity (.NET 10) + OpenIddict | `MIT / Apache 2.0` | Identity ako základ (MapIdentityApi, 2FA); OpenIddict keď má byť Alvo aj OIDC provider |
| Realtime | SignalR + in-process eventy (voliteľne Npgsql WAL / SQL Change Tracking) | `MIT` | Engine-agnostické; authz na push od dňa 1 |
| Automation / outbox | Wolverine (+ Hangfire/Quartz cron) | `MIT / LGPL / Apache` | Transactional outbox hotový; **nie MassTransit v9** |
| Event obálka / webhooky | CloudNative.CloudEvents + Standard Webhooks + Polly | `Apache/MIT` | Štandardy = interoperabilita |
| Storage | tusdotnet + AWSSDK.S3 / Azure Blobs / MinIO | `MIT/Apache` | TUS resumable |
| Caching | HybridCache (L1+L2) + Redis/Valkey | `MIT` | Stampede protection, tag invalidation, event-driven |
| Messaging | SendGrid/SMTP/ACS + Twilio/Vonage + FCM/APNs za rozhraním | `MIT/Apache` | Jednotné `I*Sender`, provider-swappable |
| Multi-tenancy | Finbuckle.MultiTenant + Postgres RLS / Marten | `MIT` | 3 modely izolácie za jednou abstrakciou |
| Secrets | Key Vault / Vault / K8s Secrets za `ISecretStore` | `Apache/OSS` | Managed identity, žiadne credentials v configu |
| Backup / PITR | pgBackRest / Barman / WAL-G → Blob/S3 | `MIT/OSS` | Point-in-time recovery, testovaný restore |
| M2M auth / OpenAPI | OpenIddict + AspNetCore.OpenApi + Scalar | `Apache/MIT` | Client credentials, PAT, scopes; **nie Duende** |
| Image transforms | SkiaSharp / Magick.NET | `MIT / Apache` | **Nie ImageSharp** |
| Custom logic | Roslyn scripting + AssemblyLoadContext | `MIT` | C# = host aj user kód |
| Gateway / rate limit | YARP + built-in rate limiting | `MIT` | Ekvivalent Kong |
| Observability | OpenTelemetry .NET za `ITelemetrySink` | `Apache 2.0` | Traces/metrics/logs, provider-swappable |
| Event store (audit) | Marten | `MIT` | Audit + tamper-evidence zadarmo |
| Scheduled jobs | Hangfire / Quartz.NET + distributed lock | `LGPL / Apache` | Cron, delayed, durable |
| Škálovanie stavu | Orleans / Redis backplane | `MIT` | Fáza scale-out |
| Testy | Testcontainers + xUnit | `MIT` | Reálny Postgres/Redis/MinIO v CI |

### 13.3 Čoho sa vyvarovať

- **Parse lekcia:** žiadny proprietárny formát; open-source jadro.
- **Firebase pricing šoky:** žiadny per-operation billing bez stropu.
- **Supabase realtime authz komplexnosť:** jedna politika pre všetky kanály od začiatku.
- **Directus automation diery:** condition pred behom, batch coalescing, logy bez šumu.
- **Appwrite gap:** changed-column podmienky first-class; pending eventy prežijú reštart (outbox).
- **Vlastný DSL:** jeden expression language, žiadny nový jazyk na učenie.
- **Licenčné míny:** apríl 2025 — MediatR + AutoMapper + MassTransit v9 komerčné; skôr Duende, ImageSharp, FluentAssertions, QuestPDF, Moq. Preveruj licenciu každej závislosti.

### 13.4 Minimálny „wedge" a roadmapa STRATÉGIA — mimo záberu tohto dokumentu

Tento dokument definuje **cieľový stav** — čo má kompletný BaaS obsahovať (§1–§12). Fázovanie, vstupný wedge a poradie dodávky sú *stratégia*, ktorá sa rieši osobitne; nasledujúce odseky sú len predbežná poznámka na neskoršiu diskusiu, nie súčasť produktovej definície.

> **Vstupná kombinácia**
*Embedded .NET BaaS ako NuGet knižnica*: (1) auto Data API zo schémy — SQLite v dev-mode, Postgres/Azure SQL v produkcii (§8.2), (2) rules kompilované do parametrizovaného SQL predikátu, engine-agnostické, bez závislosti na natívnom RLS (§2.4), (3) first-class multi-tenancy, (4) **unified event system s automation rules a Standard Webhooks doručovaním**, in-process (nezávislé od WAL), (5) schema-as-code + C# codegen (MCP ako voliteľný adaptér nad API). Body 2–4 dnes nemá nikto v tejto kombinácii — spolu s .NET nativitou a dev-mode zero-friction štvornásobný diferenciátor.

- **Fáza 1:** Data API + rules + multi-tenancy + event outbox + webhooky (MCP adaptér ako voliteľný neskorší doplnok nad API). Benchmark: agent postaví CRUD backend s notifikáciou „pri schválení objednávky pošli webhook" jedným promptom.
- **Fáza 2:** realtime (SignalR + in-process eventy; WAL/Change Tracking hardening voliteľne), storage (TUS + S3), plné auth flows (OAuth, passkeys), automation UI builder.
- **Fáza 3:** functions/pluginy, branching, admin studio kompletné, GraphQL.
- **Fáza 4:** .NET-native local-first sync + durable workflows (Marten/Wolverine) — trhové medzery §12.4–12.6.

**Čo by zmenilo odporúčanie:** ak Microsoft rozšíri DAB o auth/realtime/automation, wedge sa zúži → presun na local-first a event sourcing audit; ak Supabase vydá first-party .NET SDK, dôraz na self-host/on-prem a embedded model.

## §14 Príloha: stav .NET knižníc a licencie (2026)

| Knižnica | Účel | Licencia | Poznámka |
|---|---|---|---|
| Data API builder | Auto REST/GraphQL/MCP | `MIT` | Microsoft, v2.x, aktívne |
| HotChocolate | GraphQL server | `MIT` | Jadro MIT; komerčný len Nitro/Fusion |
| OpenIddict | OIDC/OAuth 2.1 | `Apache 2.0` | **Preferuj pred Duende** |
| Duende IdentityServer | OIDC | `komerčná` | Vyhni sa v OSS |
| ASP.NET Core Identity (.NET 10) / SignalR | Users / realtime | `MIT` | Built-in; Identity (.NET 10) má moderné token endpointy — Alvo na ňom stavia |
| Npgsql | Driver + logical replication | `PostgreSQL` | WAL streaming first-class |
| Marten / Wolverine | Doc DB + event store / outbox messaging | `MIT` | JasperFx „Critter Stack" |
| MassTransit | Message bus | `v9 komerčná` | v8 Apache do konca 2026; **preferuj Wolverine** |
| Hangfire / Quartz.NET | Jobs / cron | `LGPL / Apache` | Zdarma komerčne |
| CloudNative.CloudEvents | Event obálka | `Apache 2.0` | CNCF štandard, oficiálny .NET SDK |
| Polly | Retry / circuit breaker | `BSD-3` | Súčasť .NET resilience |
| tusdotnet | Resumable uploads | `MIT` | Oficiálna TUS implementácia, v2.11.2 |
| AWSSDK.S3 / Azure Blobs / MinIO | Object storage | `Apache 2.0` | Stabilné |
| ImageSharp | Image transforms | `Split License` | License key od v4 — **vyhni sa** |
| SkiaSharp / Magick.NET | Image transforms | `MIT / Apache` | Odporúčané náhrady |
| pgvector-dotnet | Vector search | `MIT` | Npgsql integrácia |
| YARP / Orleans / OpenTelemetry .NET | Gateway / actors / observability | `MIT/Apache` | Microsoft/CNCF |
| Roslyn scripting | User-defined functions | `MIT` | `Microsoft.CodeAnalysis.CSharp.Scripting` |
| Testcontainers | Integračné testy | `MIT` | Reálny Postgres/SQL Server v CI |
| Microsoft.Testing.Platform (MTP) | Test runtime | `MIT` | Default v .NET 10, nahrádza VSTest; zrelé. Runtime, nie framework |
| xUnit v3 | Test framework | `Apache 2.0` | Natívna MTP podpora, stabilné 1.0+. TUnit zvážený, odložený (pred 1.0) |
| Shouldly / AwesomeAssertions | Assertions | `MIT / Apache` | Náhrady za FluentAssertions v8+ (komerčný) |
| NSubstitute | Mocking / fakes | `BSD` | Mockovanie portov |
| CsCheck | Property-based testy | `Apache 2.0` | CEL→SQL invarianty, API invarianty naprieč projektmi |
| Verify | Snapshot testy | `MIT` | OpenAPI, CEL→SQL, migrácie; Verify.HeadlessBrowsers pre admin UI |
| PublicApiGenerator / Roslyn PublicApiAnalyzers | Public API approval | `MIT / Apache` | Breaking-change gate na Abstractions |
| NetArchTest.Rules | Architektúrne testy | `MIT` | Pravidlá závislostí balíkov, encapsulation, vertical slice |
| Stryker.NET | Mutation testy | `Apache 2.0` | Len security-core, path-filtered v PR |
| Vacuum | OpenAPI linting | `MIT` | Go binárka (žiadny Node), Spectral-kompatibilné rulesety; nie Spectral (dormantný) |
| Microsoft.Playwright (.NET) | Admin E2E | `Apache 2.0` | Blazor dashboard flows; Verify.HeadlessBrowsers na vizuálne snapshoty |
| TeaPie | E2E (API demo) | `MIT` | Kros-sk; čierna skrinka nad bežiacim demo API |
| dotnet-affected | Test scoping (dev tool) | `MIT` | Affected projekty z git diff; scopuje integračné testy v PR |
| FluentAssertions v8+ | Assertions | `komerčná` | Xceed licencia od jan 2025 — vyhni sa; v7 Apache. Použi Shouldly |
| MediatR / AutoMapper | Mediator / mapping | `komerčná` | Dual-license od apr 2025 — vyhni sa v jadre |

## §15 Caveats

- **Star counts a financovanie sú približné** (stav jún 2026); pri finálnom rozhodnutí over oficiálne zdroje. Riziko zámeny „Convex" v databázach financovania s inými firmami rovnakého mena.
- **Detaily automation platforiem sa hýbu:** Appwrite gap na changed-column podmienky a Directus per-item správanie sú stav k researchu — pred návrhom over aktuálne release notes.
- **Directus licencia je pohyblivá** (GPLv3 → BUSL → MSCL-1.0, enforcement od v12); zdarma pod $5M revenue / 50 zamestnancov.
- **Časť porovnaní pochádza z vendor blogov** — marketingový bias; technické fakty (RLS, WAL, TUS, pg_net, event payloady) overené z primárnych dokumentácií.
- **.NET licencie sa menia rýchlo** — preveruj licenčný text každej závislosti tesne pred commitom.
- **Akceptačné kritériá v §2–§3 sú návrh latky**, nie priemyselná norma — čísla (latencie, limity) kalibruj vlastnými benchmarkmi na cieľovom hardvéri.

## §16 Príklad — vlastné CRM v oboch režimoch

Konkrétny scenár, ktorý prepája celý dokument: firma si stavia vlastné CRM — firmy, kontakty, obchodné prípady (deals), aktivity. Obchodník vidí len svoje deals, manažér celý tím; výhra dealu spustí webhook do fakturácie a e-mail; uzavretý deal sa nedá meniť. Najprv čisto deklaratívne cez Docker + JSON descriptor (režim 1, §2.14), potom to isté vo vlastnom hoste s C# rozšíreniami (režim 2).

### 16.1 Režim 1 — Docker image + JSON descriptor

Celý backend je jeden súbor `crm.alvo.json`. Spustenie:

```
docker run -p 8080:8080 \
  # bootstrap prvého admina (infra, nie descriptor)
  -e ALVO_ADMIN_EMAIL=admin@firma.sk \
  -e ALVO_ADMIN_PASSWORD_FILE=/run/secrets/alvo_admin \
  # admin portál: cesta, zapnutie, bezpečnostné brzdy
  -e ALVO_ADMIN__ENABLED=true \
  -e ALVO_ADMIN__PATH=/admin \
  -e ALVO_ADMIN__ALLOWED_IPS=10.0.0.0/8 \
  -e ALVO_SCRIPTS_ALLOW_UI_EDIT=false \
  -e ALVO_DB__PROVIDER=postgres \
  -e ALVO_DB__CONNECTIONSTRING=... \
  -v ./crm.alvo.json:/alvo/descriptor.json \
  mmlib/alvo
# → funkčné CRM API + dashboard na :8080, bez jediného riadku kódu
```

Descriptor (skrátený — podmienky sú CEL, transformácie JSONata, viď §3.3):

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "name": "crm",
  "auth": {
    "providers": [ "local", "google" ],
    "roles": [ "sales", "manager" ]
  },
  "admin": {
    "access": {
      "admin":     "@user.role == 'manager' && @user.email.endsWith('@firma.sk')",
      "developer": "@user.role == 'manager'",
      "viewer":    "@user.role in ['sales','manager']"
    },
    "branding": { "title": "Firma CRM", "logoUrl": "/assets/logo.svg" }
  },
  "entities": {
    "companies": {
      "fields": {
        "name":   { "type": "string", "required": true },
        "ico":    { "type": "string", "unique": true },
        "owner_id": { "type": "ref", "entity": "users" }
      },
      "rules": {
        "list":   "@user.role in ['sales','manager']",
        "create": "@user.role in ['sales','manager']",
        "update": "@user.role == 'manager' || owner_id == @user.id"
      }
    },
    "contacts": {
      "fields": {
        "company_id": { "type": "ref", "entity": "companies", "required": true },
        "name":  { "type": "string", "required": true },
        "email": { "type": "string", "format": "email" }
      },
      "rules": { "list": "@user.id != null", "create": "@user.id != null" },
      "hooks": {
        "beforeCreate": [
          { "condition": "has(new.email)",
            "action": { "mutate": { "email": "lower(new.email)" } } }
        ]
      }
    },
    "deals": {
      "fields": {
        "company_id": { "type": "ref", "entity": "companies", "required": true },
        "title":  { "type": "string", "required": true },
        "amount": { "type": "decimal" },
        "stage":  { "type": "enum", "values": ["lead","offer","won","lost"], "default": "lead" },
        "owner_id": { "type": "ref", "entity": "users" }
      },
      "rules": {
        "list":   "@user.role == 'manager' || owner_id == @user.id",
        "update": "@user.role == 'manager' || owner_id == @user.id"
      },
      "hooks": {
        "beforeUpdate": [
          { "condition": "old.stage in ['won','lost'] && @user.role != 'manager'",
            "action": { "reject": "Uzavretý deal môže meniť len manažér" } }
        ]
      }
    },
    "activities": {
      "fields": {
        "deal_id": { "type": "ref", "entity": "deals", "required": true },
        "kind":    { "type": "enum", "values": ["call","email","meeting"] },
        "note":    { "type": "text" },
        "due_at":  { "type": "datetime" }
      },
      "rules": { "list": "@user.id != null", "create": "@user.id != null" }
    },
    "invoices": {
      "fields": {
        "deal_id":    { "type": "ref", "entity": "deals", "required": true },
        "number":     { "type": "string", "required": true, "unique": true },
        "issued_on":  { "type": "date", "required": true },
        "net_total":  { "type": "decimal", "precision": 12, "scale": 2,
                        "rollup": { "from": "invoice_items", "op": "sum", "field": "line_total" } },
        "vat_total":  { "type": "decimal", "precision": 12, "scale": 2, "readOnly": true },
        "gross_total":{ "type": "decimal", "precision": 12, "scale": 2,
                        "computed": "net_total + vat_total" }
      },
      "rules": { "list": "@user.id != null", "create": "@user.role == 'manager'" },
      "hooks": {
        "beforeCreate": [
          { "action": { "mutate": { "vat_total": "round(net_total * vatRate(issued_on, @tenant.country), 2)" } } }
        ]
      }
    },
    "invoice_items": {
      "fields": {
        "invoice_id": { "type": "ref", "entity": "invoices", "required": true, "onDelete": "cascade" },
        "label":      { "type": "string", "required": true },
        "unit_price": { "type": "decimal", "precision": 12, "scale": 2, "required": true },
        "amount":     { "type": "decimal", "precision": 10, "scale": 2, "required": true },
        "line_total": { "type": "decimal", "precision": 12, "scale": 2,
                        "computed": "unit_price * amount" }
      },
      "rules": { "list": "@user.id != null", "create": "@user.role == 'manager'" }
    }
  },
  "automation": [
    {
      "name": "deal-won",
      "trigger": { "event": "entity.deals.updated" },
      "condition": "changed(stage) && new.stage == 'won'",
      "actions": [
        { "type": "webhook", "endpoint": "invoicing",
          "payload": "{ 'dealId': new.id, 'amount': new.amount, 'company': new.company_id }" },
        { "type": "email", "template": "deal-won",
          "to": "{{@owner.email}}" }
      ]
    },
    {
      "name": "stale-deal-reminder",
      "trigger": { "schedule": "0 8 * * MON" },
      "actions": [
        { "type": "function", "name": "remind-stale-deals" }
      ]
    }
  ],
  "webhooks": {
    "endpoints": {
      "invoicing": { "url": "https://erp.firma.sk/hooks/alvo", "secretRef": "invoicing-webhook-secret" }
    }
  }
}
```

Rozdelenie zodpovedností pri admin portáli (§2.14): **env** nesie infra a bezpečnostné brzdy — bootstrap credentials prvého admina, zapnutie/cesta portálu, IP allowlist, gate na UI editáciu skriptov; **descriptor** nesie definíciu — mapovanie aplikačných rolí na úrovne dashboardu (admin/developer/viewer, §2.8) cez CEL výrazy a branding. Prístupové pravidlá sú tak verzovateľné v Gite spolu so zvyškom backendu, credentials nikdy.

Ten istý descriptor sa dá aplikovať aj bez Dockera — cez CLI (`alvo apply crm.alvo.json`), Management API, alebo ho **celý vygeneruje agent** z prompt-u „postav mi CRM s dealmi, kde obchodník vidí len svoje" — descriptor má JSON Schema, takže agent (Claude Code, Cursor…) generuje validný súbor priamo, bez akéhokoľvek protokolu navyše (§9).

### 16.2 Režim 2 — vlastný host s konfiguráciou v C#

Rovnaké CRM, ale ako súčasť vlastnej appky: schéma v C# triedach, deklaratívne pravidlá z descriptora sa *kombinujú* s programovými rozšíreniami — vlastný scoring modul, C# hooky s prístupom k službám hostu, Entra ID, vlastný endpoint.

```json
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAlvo(alvo => alvo
    .UseAzure()                                        // KeyVault, Blobs, Service Bus, App Insights (§1)
    .UseDatabase(db => db.UseSqlServer(
        builder.Configuration.GetConnectionString("Crm")))
    .UseTenancy(t => t.ResolveFromSubdomain())         // multi-tenant CRM (§4)
    .UseAuth(a => a
        .AddEntraId(builder.Configuration)             // IIdentityProvider (§2.2)
        .AddRole("sales").AddRole("manager"))
    .FromDescriptor("alvo/crm.alvo.json")              // deklaratívna časť — TEN ISTÝ súbor ako v režime 1
    .UseAdmin(admin => admin                           // admin portál (§2.8)
        .Path("/alvo/admin")
        .Access(a => a                                 // mapovanie rolí hostu na úrovne dashboardu
            .Admin(u => u.IsInRole("it-admin"))
            .Developer(u => u.IsInRole("manager"))
            .Viewer(u => u.IsInRole("sales")))
        .Enabled(builder.Environment.IsDevelopment()
                 || builder.Configuration.GetValue<bool>("Alvo:Admin:Enabled"))
        .AllowScriptUiEdit(false))                     // csx UI editácia vypnutá (§2.6)
    .AddModule<DealScoringModule>()                    // vlastný modul: entita + služby + endpointy
    .Hooks(h =>
    {
        // C# hook — logika, ktorá sa do CEL nezmestí: volanie internej služby
        h.BeforeUpdate<Deal>(ctx =>
            ctx.Old.Stage is "won" or "lost" && !ctx.User.IsInRole("manager")
                ? HookResult.Reject("Uzavretý deal môže meniť len manažér")
                : HookResult.Continue());

        // after-hook: post-commit, durable, sieť povolená — sync do interného ERP
        h.AfterUpdate<Deal>(async (ctx, ct) =>
        {
            if (ctx.Changed(d => d.Stage) && ctx.Record.Stage == "won")
                await ctx.Services.GetRequiredService<IErpClient>()
                         .CreateInvoiceDraftAsync(ctx.Record, ct);
        });
    })
);

var app = builder.Build();
app.MapAlvo("/api");                                   // Data API + realtime + admin

// vlastný endpoint mimo CRUD — plný ASP.NET Core s Alvo kontextom (§10 escape hatch)
app.MapGet("/api/reports/pipeline", async (IAlvoData data, AlvoContext ctx) =>
{
    var deals = await data.QueryAsync<Deal>(q => q
        .Where("stage in ['lead','offer']")            // CEL — policy sa vynúti automaticky
        .OrderBy(d => d.Amount, desc: true), ctx);
    return Results.Ok(deals.GroupBy(d => d.Stage)
        .Select(g => new { stage = g.Key, total = g.Sum(d => d.Amount) }));
});

app.Run();
```

```json
// Schéma ako kód — alternatíva/doplnok k descriptoru (obe cesty plnia ten istý schema registry)
[AlvoEntity]
public class Deal
{
    public Guid Id { get; set; }
    [AlvoRef(typeof(Company))] public Guid CompanyId { get; set; }
    public string Title { get; set; } = "";
    public decimal Amount { get; set; }
    [AlvoEnum("lead","offer","won","lost"), AlvoDefault("lead")]
    public string Stage { get; set; } = "lead";
    [AlvoRef(typeof(AlvoUser))] public Guid OwnerId { get; set; }
}
```

> **Čo príklad demonštruje**
- **Ten istý descriptor v oboch režimoch** — `crm.alvo.json` mountnutý do Dockera aj načítaný cez `FromDescriptor()`; migračná cesta režim 1 → režim 2 je „prenes súbor" (§2.14).
- **Deklaratívne + programové sa skladá, nie nahrádza** — CEL pravidlá a hooky z descriptora platia ďalej, C# hooky pridávajú to, čo sa do výrazu nezmestí (volanie IErpClient, DI).
- **Hranice jazykov v praxi:** podmienky = CEL (kompilované do SQL — `list` pravidlo dealov filtruje v databáze), transformácia webhook payloadu = JSONata, C# = plná logika v hookoch a moduloch (§3.3).
- **Policy sa nedá obísť ani vo vlastnom kóde** — custom endpoint číta cez `IAlvoData` s `AlvoContext`, takže obchodník dostane v reporte len svoje deals (§8.2 trust model).
- **Tri vrstvy dopočtu na faktúre (§2.1):** `invoice_items.line_total = unit_price * amount` je *computed field* (generated column, aritmetika nad riadkom); `invoices.net_total = sum(invoice_items.line_total)` je *rollup* (transakčne konzistentná agregácia cez položky); `vat_total` ide cez *before-hook*, lebo sadzba je kontextová a časovo platná (`vatRate(issued_on, @tenant.country)`) — nie je to aritmetika, ale biznis logika; `gross_total = net_total + vat_total` je zas triviálny computed. Presne rozhodovací rebríček zo §2.1 v jednom príklade.

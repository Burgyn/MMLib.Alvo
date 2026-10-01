# Automation, hooks, vlastné funkcie a editory v dashboarde — jedna analýza

Stav: 2026-10-01. Autor: architect-analýza (read-only nad repom). Zdrojový strom: `main` @ `ee0a6d4`, dashboard z `origin/f5/admin-dashboard` @ `6aea12a`.
Čo som skutočne čítal a čo nie je na konci (sekcia 11). Kde neviem, píšem "NEVERIFIKOVANÉ".

---

## 0. Zhrnutie v desiatich bodoch

1. Pod názvom "automation" sú dnes **štyri rôzne veci** s rôznou dôverou: CEL výrazy (in-tx, čisté), deklaratívne hooky (before = CEL reject/mutate; after = webhook/email), ECA blok `automation` + `functions` (csx) a `webhooks`/`templates`. Len prvé dve bežia. `automation`, `functions` sú v `UnhonouredSubsystems` (`src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs:127,145`) — **parsované, nikdy nevyhodnotené**.
2. Dashboard dnes nemá žiadny editor výrazov: každý CEL vstup je `MudTextField` (`HooksTab.razor:123`, `RulesTab.razor:79`, `FieldEditor.razor:246`). Je to 5+ kópií toho istého nič-nerobiaceho poľa.
3. CEL v Alvo je **uzavretý, ručne písaný AST** bez funkčnej tabuľky: jediné volania sú `lowerAscii` a `now()`, len v profile `Mutate` (`CelTypeChecker.cs:145,694-707`, `docs/architecture/cel.md:121-163`). "Host-registered function" teda nie je rozšírenie, ale **nová os celého expression subsystému**.
4. Tvrdá otázka (SQL vs in-process) sa rozhoduje **profilom**, nie funkciou: `Rule` a `Computed` sa renderujú do SQL (`_sqlRenderedProfiles`, `CelTypeChecker.cs:~150`), `Condition`, `Mutate`, `Access` sú interpreter-only. Funkcia teda potrebuje **triedu vyhodnotenia** (`InProcess` | `SqlTranslatable`) a z nej sa odvodia povolené profily.
5. Odporúčanie: **jeden `ICelFunctionCatalog`** (Abstractions) = jediný zdroj pravdy pre type checker, interpreter, SQL renderer, Management API `cel/functions`, dashboard editor aj AI skills. Fáza 1 len `InProcess` pre `Condition` + `Mutate`; `SqlTranslatable` (Rule/Computed) až fáza 3, s povinnou conformance sadou voči interpreteru.
6. Rule s in-process funkciou je **zakázané natvrdo**: spec §2.4 / `alvo-specifikacia.md:135` — autorizácia ide do SQL WHERE, nikdy post-filter v pamäti.
7. csx (`functions` blok, #34/#35) je **iná trust trieda**: admin-level kód, nie safe-by-construction; UI-edit je opt-in `ALVO_SCRIPTS_ALLOW_UI_EDIT`, default off (baas-analyza §2.7). Dashboard csx editor teda **nesmie** byť súčasťou expression editora ani prvého rezu.
8. #272 + #273 + #276 + #28 sú jedna téma s jedným poradím; #33/#34/#35/#120/#152/#149 sú **backendová** polovica automation, ktorú dashboard builder (#28) nemá na čom postaviť. Navrhujem 9 PR v 4 vlnách (sekcia 9).
9. Medzera, ktorú treba pomenovať: spec F4 sľubuje "Automation minimal (ECA + cron + email)", #22 je CLOSED, ale blok `automation` sa nevyhodnocuje. Hooky (after) sú de facto jediná bežiaca ECA. Dashboard "automation builder" by dnes autoroval niečo, čo build ignoruje.
10. Päť rozhodnutí pre maintainera (sekcia 8): (D1) jeden catalog + trieda vyhodnotenia, (D2) zákaz in-process funkcií v `Rule`/`Computed` do fázy 3, (D3) zdroj pravdy pre editor = core endpointy `cel/*`, nie druhý parser v UI, (D4) CodeMirror 6 cez JS interop vs vlastný MudBlazor autocomplete, (D5) čo robí `automation` blok (vyhodnotiť, alebo zlúčiť s after-hookmi).

---

## 1. Aktuálny stav s počtami

### 1.1 Čo existuje kde

| Oblasť | `main` | `origin/f5/admin-dashboard` | Nič |
|---|---|---|---|
| CEL parser/type checker/interpreter/SQL renderer | `src/MMLib.Alvo/Expressions/Internal/*` (~3 600 riadkov: CelTypeChecker 874, CelInterpreter 646, CelParser 511, SqlPredicateRenderer 493) | rozdiel vs main len v type checkeri/compileri (vetvy sa rozišli; main má novšie fixy, napr. #287 oblasť) | funkčná tabuľka, registry, `ICelFunctionCatalog` |
| CEL porty | `ICelCompiler`, `IPredicateRenderer`, `IPredicateEvaluator`, `IFieldSqlRenderer` (default interface members), `CelProfile` {Rule, Computed, Condition, Mutate, Access} | rovnaké | `ICelFunctionCatalog`, `cel/check`, `cel/evaluate`, `cel/functions` |
| Hooky (descriptor) | 6 hook pointov honoured; before: `reject`, `mutate`; after: `webhook`, `email`; `function`, `http.call`, `entity.update` **refused** (`UnhonouredFeatures.EveryActionType`) | tab "On write": add/remove, žiadna editácia na mieste | C# tvár hookov (`IBeforeHookRunner` je len port na behu, bez `AddHook` na builderi; `IAlvoBuilder` má jediný člen `Services`) |
| `automation` (ECA) | schéma + model + mapper; **warned, nevyhodnocuje sa** | stránka "Not yet" (`NotYet.razor`) | evaluator, scheduler, `trigger.event` wildcardy (refused, #153) |
| `functions` (csx) | schéma (`script`, `trigger.http/schedule/event`, `execution`); **warned** | stránka "Not yet" | `MMLib.Alvo.Scripting`, `IFunctionRuntime` |
| `webhooks.endpoints` | after-hook posiela (bez HMAC, bez `secretRef`, bez DLQ — #120, #152, 7.1/#33) | Integrations: read-only JSON | signing, retries, DLQ, delivery log UI |
| `templates` | after-hook `email` renderuje `{{…}}`; `bodyFile` refused | Integrations: read-only | editor šablón |
| JSONata | schéma `$defs/jsonata`; raw JSONata **refused** (`UnhonouredFeatures.RawJsonata`), len `{{…}}` sugar | — | evaluator (#149) |
| Management API | 10 routes (`ManagementEndpoints.cs`) vrátane `/capabilities`, `/policy/simulate` | 18 routes (+ users/ai) | `cel/*` |
| Dashboard | `src/MMLib.Alvo.Admin` má **1 `.cs`** (`AlvoAdminAssets.cs`) | 196 súborov mimo wwwroot, **69 `.razor`** | — |
| AI asistent | — | `src/MMLib.Alvo.Ai` (28 súborov), skills v `.claude/skills/alvo-descriptor-*` (hooks, rules-and-cel, computed-and-rollups …) | skill o host-funkciách |

Dashboard je teda **mimo main** (824 súborov / +131k riadkov v diffe); všetko "dashboard" nižšie sa dá merať len proti vetve.

### 1.2 Audit dashboardu vs. build (z `docs/todo-admin.md` §8, 115 kľúčov: 34 edit / 23 read / 38 said / 20 gap)

Relevantné riadky pre túto tému:

| Kľúč | Build | Dashboard | Poznámka |
|---|---|---|---|
| `hooks.*` (6 pointov) | honoured | edit — **len add/remove** | #276 |
| before `mutate.<f>` literál | honoured | gap (všetko sa zabalí do `$cel`) | `HookBuilder.cs:177` |
| before `mutate` multi-field | honoured | gap (1 pole na hook) | `HookBuilder.cs:34,79` |
| `webhook.endpoint`, `email.template` | honoured | free text, endpointy/šablóny sa nedajú deklarovať | #276 |
| `webhook.payload` (`{{…}}`) | honoured | gap (tab ho zadržal ako "refused", refused je len raw JSONata) | todo §8a |
| `automation.*` (7 kľúčov) | warned | said (po oprave `automation` vs `automations`, položka 20) | |
| `functions.*` (6 kľúčov) | warned | said | |
| `computed`, `rollup` | honoured | edit (Value kind, položka 14) — `computed` je plain textarea | |
| rules (`rules.*`) | honoured | edit + simulátor | `RulesTab.razor:79` |

### 1.3 Frozen artefakty, ktoré tvar obmedzujú

* `schema/project.schema.json`: `$defs/cel` je `string` 1–2000 znakov; `celExpr` = `{"$cel": "..."}`; `valueOrExpr` pre `mutate` a `entity.update.payload`; `jsonata` string ≤ 8000. Schéma **nemá miesto pre funkcie hostu** — a nemá ho mať: host funkcie sú kód, nie descriptor (sekcia 2.3). Descriptor ich len *referencuje* vo výrazoch.
* `CelNode` je `public abstract record` (Abstractions), ale `CelCall` je `internal` (`src/MMLib.Alvo/Expressions/Internal/CelTree.cs:63`, `CelCall(string Name, CelNode? Argument)` — **jediný argument**). Zmena na N-árne volanie je teda zmena interného stromu, ale každý walker sa musí prepísať — preto #85 (uzavrieť hierarchiu) je predpoklad.
* `CelValueType` = {Bool, Int, Decimal, String, Timestamp, Uuid, Json, StringList, Null}. **Neexistuje `Date`** — príklad z #272 (`DateOnly date`) sa nedá vyjadriť bez rozšírenia typového systému.

---

## 2. Konceptuálny model

### 2.1 Päť vecí, ktoré sa miešajú

| Vec | Čo to je | Kde beží | Trvanie | Trust | Popisuje descriptor? |
|---|---|---|---|---|---|
| **A. Výraz (CEL)** | predikát/hodnota nad riadkom, `old`/`new`, `@user`, `@tenant` | `Rule`, `Computed` → **SQL**; `Condition`, `Mutate`, `Access` → **interpreter in-tx** (after-hook `Condition` nad envelope po commite) | µs–ms | safe-by-construction (non-Turing, uzavretá gramatika, fail-fast pri apply) | áno (`$cel`, `condition`, `rules`) |
| **B. Hook akcia** | `reject`/`mutate` (before, in-tx) alebo `webhook`/`email` (after, outbox) | pipeline v jadre | before: rozpočet ms, **žiadna sieť**; after: durable, retries | deklaratívny katalóg = dôverný; efekty sú mimo výrazu | áno (`entities.*.hooks`) |
| **C. Endpoint / šablóna** | spravovaný cieľ doručenia (`webhooks.endpoints`) a text správy (`templates`) | after-side dispatcher | sekundy, retries | egress guard (SSRF) už je (`WebhookEgressGuard`), HMAC nie | áno, ale **iná kategória**: sú to pomenované zdroje, ktoré hooky *referencujú* |
| **D. ECA pravidlo** | trigger (event/cron) + condition + actions | **nevyhodnocuje sa** | — | rovnaká ako B | áno (`automation`) |
| **E. csx funkcia / `IFunctionRuntime`** | Roslyn skript, trigger HTTP/event/schedule, vlastný kód | in-process ALC / sidecar / microVM (dve osi, baas-analyza §2.7) | sekundy, async cez outbox | **admin-level plná dôvera; nie sandbox** | áno (`functions`), kód je v bundli |
| **F. Host CEL funkcia (nové, #272)** | C# delegát s typovaným podpisom volaný z výrazu | rovnako ako A | µs | **dôveryhodný kód hostu** (embedded), ale kontrakt "čistá" je len deklarovaný | **nie** — registrácia je v kóde; descriptor ju len volá |

Hranice dôvery (kde sa trust mení): (1) descriptor → core: všetko sa kompiluje fail-fast, dôveruje sa len gramatike; (2) core → host delegát (F): dôvera v kód hostu, nie v autora descriptora; (3) core → csx (E): dôvera v admina, ktorý skript nahral; (4) core → externý svet: len after-side, cez egress guard.

Kľúčový dôsledok: **autor descriptora (agent alebo operátor z dashboardu) vie volať F, ale nikdy ho nevie vytvoriť.** Preto F je v bezpečnostnom zmysle blízko vstavaným funkciám (`lowerAscii`), nie csx. To je argument za jednu registráciu a proti tomu, aby sa F implementovalo cez csx.

### 2.2 Rebrík (už v spec/analýze) a kam F zapadá

`computed` → `rollup` → before-hook `mutate` → after-hook/akcia → csx (`alvo-specifikacia.md:334`, `baas-analyza §10.1`). F nie je nový stupeň, je to **rozšírenie slovníka výrazov** na stupňoch computed/mutate/condition. #272 v bode 1 navrhuje aj "computed at write" ako nový stupeň — odporúčam ho **neotvárať** (sekcia 8, D2).

### 2.3 Čo hovorí descriptor vs. čo žije v kóde hostu

| Údaj | Descriptor | Kód hostu |
|---|---|---|
| Volanie `vat_rate(country, kind, ts)` vo výraze | áno | — |
| Podpis, typy, čistota, docs, SQL šablóny | **nie** | áno (registrácia) |
| Zoznam dostupných funkcií | — (odvodený: Management API `cel/functions`) | — |
| Endpoint URL + `secretRef` | áno | hodnota secretu v `ISecretStore` |
| csx skript | cesta v `functions.*.script`, obsah v bundli | — |

Dôsledok pre GitOps: descriptor zapísaný pre hosta A (ktorý registruje `vat_rate`) a aplikovaný na hosta B (štandalone, bez nej) je **odmietnutý pri apply** so štruktúrovanou chybou "unknown function `vat_rate`; registered: …". To je správne (fail-fast, rovnako ako neexistujúci stĺpec; analyza §2.4 akceptačné kritérium) a musí byť zdokumentované ako vlastnosť embedded-only.

---

## 3. Registry host funkcií — návrh

### 3.1 Tvar (Abstractions, verejný len čo je kontrakt)

```csharp
// návrh, nie záväzné API — názvy a tvar rozhodne PR
public interface ICelFunctionCatalog
{
    IReadOnlyList<CelFunctionDescriptor> Functions { get; }
    bool TryResolve(string name, IReadOnlyList<CelValueType> args, CelProfile profile,
                    out CelFunctionOverload overload);
}

public sealed record CelFunctionDescriptor(
    string Name,                         // [a-z][a-zA-Z0-9]* , nie kolízia so vstavanými ani s has/changed
    IReadOnlyList<CelFunctionOverload> Overloads,
    string Summary, string? Remarks,
    IReadOnlyList<CelFunctionExample> Examples,   // expression + expected value -> aj conformance vstup
    CelFunctionProvenance Provenance);            // Builtin | Host

public sealed record CelFunctionOverload(
    IReadOnlyList<CelParameter> Parameters,       // (name, CelValueType, doc)
    CelValueType Result,
    CelEvaluation Evaluation,                     // InProcess | SqlTranslatable
    CelPurity Purity,                             // Pure | ContextBound (now)
    CelCost Cost);                                // Constant | LinearInInput  (informatívne + budget)
```

Registrácia na builderi: `builder.AddCelFunction("vat_rate", (string country, string kind, DateTimeOffset on) => …, o => o.Describe(…).Example(…).InProcess())`. `IAlvoBuilder` má dnes len `Services` — rozšírenie je extension metóda v jadre (nie nový člen rozhrania; tým sa nezavrie nič dopredu).

### 3.2 Prečo takto (a čo to uzatvára)

* **Prior art**: CEL spec — extension funkcie sú implementation-defined, "usually highly curated", musia byť **bez pozorovateľných vedľajších efektov**, overloady s **neprekrývajúcimi sa** typmi argumentov; "time and space cost … is an essential part of the specification". cel-go: `cel.Function(name, cel.Overload(id, argTypes, resultType, binding))`, `FunctionDocs`/`OverloadExamples`, cost estimation (`CostTracking`/`EstimateCost`). cel-net (`telus-labs`) má `RegisterFunction(name, paramTypes, fn)` pred parse. Alvo preberá **tvar deklarácie** (name + typované overloady + docs + príklady + cost) a deviuje len v tom, že vyhodnotenie nejde cez cudziu knižnicu (spec §0 princíp 6: "preberáme spec a syntax, nie knižnicu").
* **Typované overloady** dávajú type checkeru presne to, čo potrebuje: arita a typy sa overia pri apply, nie za behu v transakcii (rovnako ako dnes `lowerAscii` argument — `docs/architecture/cel.md:129`).
* **`Evaluation` je súčasť podpisu**, lebo profil sa dá odvodiť: `InProcess` ⇒ `Condition`, `Mutate` (a `Access`, ak neberie riadok); `SqlTranslatable` ⇒ + `Rule`, `Computed`.
* **Scalar-only podpis je štrukturálna ochrana**: delegát dostane len hodnoty typu `CelValueType` — nie riadok, nie `AlvoContext`, nie DI scope. Nedosiahne teda tenant ani identitu volajúceho (security-core relevantné, sekcia 7). Pozor: closure delegáta môže zachytiť čokoľvek — dôvera v host kód je predpoklad, nie vlastnosť.
* **Žiadne `Task`**: prijímame len synchrónne delegáty. Zakazuje to idiomatické `await` na sieť; nezakazuje to `.Result` — preto je zákaz siete pri host funkciách **deklaratívny + merateľný**, nie štrukturálny (deviácia od "structurally impossible", spec §3.3; zapísať ako D2/D6).

### 3.3 Determinizmus a `now()`

Vstavané `now()` je **viazaný okamih** (jeden na zápis; `CelInterpreter.cs:189-257`, `cel.md:~150`), nie čítanie hodín. Host funkcia **nesmie čítať hodiny ani RNG** a ak potrebuje čas, dostane ho ako argument (`vat_rate(country, kind, now())`). Dôvod: reprodukovateľnosť simulátora/evaluátora a zhoda interpreter ↔ SQL. Registrácia deklaruje `Purity.Pure`; `ContextBound` je rezervované pre vstavané.

### 3.4 Čo registry napája

| Konzument | Čo čerpá | Poznámka |
|---|---|---|
| (1) type checker + parser | `TryResolve(name, argTypes, profile)`; nahrádza hardcoded `CelCall.LowerAscii/Now` (`CelParser.cs:417-460`, `CelTypeChecker.cs:694-718`) | vstavané `lowerAscii`, `now` sa presunú do toho istého catalogu s `Provenance=Builtin` — jedna cesta, nie dve |
| (2) interpreter | `overload.Invoke(args)` | výnimka z delegáta → rovnaká doktrína ako dnes (null-collapse vs. fail-closed) musí byť rozhodnutá; návrh: výnimka = zápis zlyhá s RFC 7807 (nie ticho null) — **otvorená otázka** |
| (3) SQL renderer | len pre `SqlTranslatable`; šablóna cez dialekt, **nie `if` v jadre** | sekcia 3.5 |
| (4) Management API | `GET {m}/projects/{p}/cel/functions` (+ `cel/scope`, `cel/check`, `cel/evaluate`) | sekcia 5.2; dnes `CapabilityReport` je precedens pre "build hovorí, čo vie" |
| (5) dashboard editor | completion, signature help, diagnostics, try-it | sekcia 4 |
| (6) AI asistent | generovaná sekcia skillu `alvo-descriptor-rules-and-cel` / `…-hooks` | skills sú `.claude/skills/alvo-descriptor-*` (embedded do `MMLib.Alvo.Ai`); zoznam funkcií sa nesmie písať ručne — drift, ktorý audit v todo-admin opakovane našiel |

### 3.5 Engine-agnosticita: SQL-prekladateľná vs. in-process (tvrdá otázka)

**Dôkazy z kódu:**

1. `Rule` a `Computed` sa renderujú do SQL: `_sqlRenderedProfiles = {Rule, Computed}` (`CelTypeChecker.cs`), a `Computed` sa stane stored generated column (`ComputedColumnSql.cs` v `Data.EntityFrameworkCore`). `Condition`, `Mutate`, `Access` sú interpreter-only; `Mutate` a `Access` renderer **odmieta menom** už pred prechodom stromu (`SqlPredicateRenderer.cs:~128-160`, oprava reálnej diery, ktorú `cel.md` opisuje).
2. SQL štruktúru skladá jediný `SqlPredicateRenderer`; dialekt sa pripája len cez `IFieldSqlRenderer` — 10 členov, nové ako **default interface members** (`RenderTwoValued`, `RenderBooleanFieldAsPredicate`, `RenderBooleanPredicate`), pričom T-SQL fake (`TSqlSeamTests`) dokazuje, že seam stačí aj pre engine bez boolean typu (`cel.md:~296-355`). Shipnuté sú SQLite + PostgreSQL; spec §0 princíp 3 vyžaduje aj Azure SQL.
3. Rule = "do SQL WHERE, nikdy post-filter v pamäti" (`alvo-specifikacia.md:135`; baas-analyza §2.4).
4. Postgres: generated column vyžaduje **immutable** funkcie (PG docs: "The generation expression can only use immutable functions…"; virtual stĺpce navyše len vstavané). Alvo nedeploy­uje DB funkcie, takže funkcia v `Computed` musí byť **vložená ako výraz** (šablóna), nie volaná ako UDF.
5. Interpreter a SQL musia zhodnúť (diferenciálny backend test; dvojhodnotová null-fold; ordinálne vs. kolačné porovnanie — známe riziko v `cel.md`).

**Rozhodnutie (odporúčané), vyplýva z dôkazov:**

| Trieda | Čo registrácia dodá | Povolené profily | Cena |
|---|---|---|---|
| `InProcess` | iba C# delegát | `Condition`, `Mutate`, (`Access` ak argumenty nie sú riadok) | nízka; **fáza 1** |
| `SqlTranslatable` | delegát **+** SQL šablóna **pre každý shipnutý dialekt** (SQLite, PostgreSQL, a T-SQL fake v testoch) + sada príkladov | + `Rule`, `Computed` | vysoká; **fáza 3** |

Ako sa šablóna dostane k rendereru bez `if` v jadre: `IFieldSqlRenderer` dostane **default** člen `string? DialectId` (additívne, nič sa nezlomí) a catalog drží `IReadOnlyDictionary<dialectId, SqlTemplate>`; renderer sám skladá `template` z už vyrenderovaných argumentov (parametre ostávajú bind-parametre — property test "preklad nikdy neinterpoluje užívateľský vstup" sa **musí rozšíriť** aj na šablóny; šablóna je kód hostu, argumenty sú používateľské).

Povinné pre `SqlTranslatable`: (a) fail-fast pri apply, ak aktívny dialekt nemá šablónu ("function `x` has no translation for `postgres`; allowed profiles: Condition, Mutate"); (b) `MMLib.Alvo.Testing` helper `CelFunctionConformance`, ktorý spustí `Examples` na interpreteri **a** na každom nainštalovanom engine a porovná (rovnaká myšlienka ako existujúci diferenciálny test); (c) `Computed` navyše vyžaduje `Purity.Pure` a `Cost.Constant`, lebo ide do DDL.

**Čo sa tým nerieši:** descriptor, ktorý používa `SqlTranslatable` funkciu v `Rule`, je prenositeľný len medzi enginmi, pre ktoré má funkcia šablónu. To nie je porušenie "engine-agnostic core" (core nič nepredpokladá), ale **je to host-level kontrakt**, ktorý dashboard musí zobraziť (badge "SQL: sqlite, postgres").

### 3.6 Čo registry nerobí

* Nenahradzuje `IFieldFormatValidator` (schéma ho spomína v `formats` popise, v kóde som ho nenašiel — NEVERIFIKOVANÉ, či je už port). Validátor formátu = dátová validácia, funkcia = výraz. Odporúčam ale rovnakú registračnú ergonómiu a rovnaký discovery endpoint (`capabilities`).
* Nie je to cesta pre I/O. Čokoľvek, čo potrebuje DB/sieť, je after-akcia alebo csx.

---

## 4. Návrh editora v dashboarde

### 4.1 Jeden komponent `<ExpressionEditor>`

Parametre: `Profile` (Rule/Computed/Condition/Mutate/Access), `Entity`, `Value`, `OnChange`, `ExpectedType`. Nahradí: rule inputs (`RulesTab`), computed (`FieldEditor`), hook condition, `mutate` hodnota, `access.*` (6 miest dnes; `HooksTab` 2×). Správa:

| Funkcia | Zdroj dát | Endpoint (návrh) |
|---|---|---|
| Completion: polia entity s typmi, `new`/`old`/`@user`/`@tenant` podľa profilu, operátory, vstavané + host funkcie | `cel/scope` | `GET {m}/projects/{p}/cel/scope?entity=&profile=` |
| Signature help (parametre, výsledok, príklady, provenance badge, "SQL: …") | `cel/functions` | `GET {m}/projects/{p}/cel/functions` |
| Diagnostics so spanmi + výsledný typ | core kompilátor | `POST …/cel/check` `{entity, profile, source}` → `{ok, resultType, errors:[{message, fixSuggestion, position, length}]}` |
| Try-it ("čo to dá?") | core interpreter nad **dodanými hodnotami** | `POST …/cel/evaluate` `{entity, profile, source, values, context}` |

`CelCompilationError(Message, FixSuggestion, Position)` už existuje — **chýba dĺžka (span end)**; treba ju pridať, aby sa dalo podčiarknuť token (Abstractions, additívne).

**Jediný CEL autorita je core** (#273, `RulesTab.razor:49` "without a second compiler"). Zámer: žiadny CEL parser v UI. Cena: jedno HTTP volanie na check (debounce 250–400 ms, lokálny token-level highlighting bez autority). Alternatíva (WASM kompilácia core) je zamietnutá — viď D3.

### 4.2 Technológia editora (D4)

| Možnosť | Pre | Proti |
|---|---|---|
| **A. CodeMirror 6 cez JS interop** (`@codemirror/autocomplete` — `CompletionSource` môže byť async; `@codemirror/lint` — `Diagnostic{from,to}`) | malý (~100 KB), mobil/touch OK (admin má mať použiteľný 375 px, analyza §2.8), jasné API pre async zdroje, ktoré volajú náš endpoint | JS v inak čisto-Blazor dashboarde; treba bundle (dashboard už má `admin.js`, `alvo.js`) |
| B. Monaco | plný language-service model | ~MBs, zlé na mobile, nadbytočné pre jednoriadkový výraz |
| C. MudBlazor `MudAutocomplete` + vlastné podčiarkovanie | žiadne JS | autocomplete nad kurzorom v texte sa dá, ale diagnostiky s rozsahmi a caret handling v `<textarea>` sú krehké; dashboard už cítil `ToJsonString` escape chyby pri jednoduchom re-renderi (todo §5f) |

**Odporúčanie: A**, jednoriadkový/dvojriadkový editor, komponent je jediné miesto s JS. Deviácia od "Blazor, jeden jazyk" (analyza §2.8) zapísať: JS je lokalizovaný v jednom komponente a rozhodnutie je spätne reverzibilné (rozhranie je `Value`/`OnChange`/`Profile`). NEVERIFIKOVANÉ: výkon interopu na reálnom Blazor Server circuit s vysokou latenciou — treba spike.

### 4.3 Hook authoring end to end (#276)

Dnešný `HookBuilder` zvláda `reject`, `mutate` (1 pole, vždy `$cel`), `webhook`, `email`. Chýba:

| Položka | Čo spraviť | Pozn. |
|---|---|---|
| Editácia na mieste | `WorkingCopy.Hooks` už má add/remove; pridať `Replace(index, node)` | uchovať záznamy, ktoré builder nevie nakresliť (princíp `FieldEditor`: preserve) |
| `mutate`: literál vs `$cel`, viac polí | prepínač "hodnota / výraz"; zoznam riadkov | `beforeDelete` ostáva reject-only (BHC) |
| Endpoint picker | zo `webhooks.endpoints` v working copy | musí zahŕňať **pending** (todo #28 vzor: čítať z working copy, nie z applied) |
| Template picker | z `templates` | `bodyFile` ostáva refused |
| Deklarácia endpointu a šablóny | Integrations: dnes read-only JSON (#271) | nutné, inak hook nejde dokončiť z dashboardu |
| `webhook.payload` ako `{{…}}` | odstrániť zbytočné zadržanie | iba raw JSONata je refused (#149) |
| Condition guidance | z `cel/scope` (point → `new`/`old`/`changed`) | dnes hardcoded v `HookBuilder.Images/Example` — duplikuje pravidlá core |

### 4.4 Automation builder — rozsah

Dnes `automation` je warned a **nevyhodnocuje sa**, takže builder (#28) by autoroval mŕtvu konfiguráciu. Odporúčam **neodomykať builder**, kým core nevyhodnocuje `automation` (D5). Dovtedy: dashboard "Automations" ostáva "Not yet" s odkazom na hooky, ktoré robia to isté pre jednu entitu. Rozdiel hook ↔ ECA (z analýzy): ECA = cron/viac-entitný/event-wildcard trigger + reťazenie + `delivery: batch`; hook = per-entita pointy. Duplicita je reálna — D5.

### 4.5 Čo editor nikdy nesmie robiť

1. **Editovať csx z UI**, kým neexistuje samostatné permission + `ALVO_SCRIPTS_ALLOW_UI_EDIT` (default off) + audit + aktivácia až po kompilácii + dry-run (baas-analyza §2.7: "kompromitovaný admin účet nesmie znamenať RCE formulárom zadarmo"). Žiadny editor, ktorý zdieľa komponent s CEL editorom, tento gate nesmie obísť.
2. Mať vlastný parser CEL / vlastné pravidlá "čo je legálne v profile" (kopírovanie `Images`/`Example` z `HookBuilder` je presne ten drift).
3. Čítať uložené riadky cez Management API pre evaluátor (deviácia D4 v `2026-09-18-f5-admin-dashboard-design.md`); hodnoty číta Data API ako operátor a posiela ich ako `values`.
4. Ponúknuť funkciu mimo jej povolených profilov (zoznam z `cel/functions` je per-profil filtrovaný).
5. Tváriť sa, že evaluátor = DB: `Computed` sa v produkcii vyhodnotí DB, evaluátor beží in-memory — UI musí povedať, **ktorý backend odpovedal** (#273 to už chce).
6. Uložiť/zobraziť secret (`secretRef` je meno, nie hodnota).

### 4.6 Policy simulator parita (RLS)

Simulátor už existuje (`POST …/policy/simulate`, `ManagementPolicyVerdict`). Prior art: Postgres RLS `USING` (filtrovanie čítania) vs `WITH CHECK` (validácia zápisu) — analyza §2.4 ich mapuje na list/get vs create/update. Akceptačné kritérium: "simulátor odpovie identicky ako produkčné vynucovanie" — to platí aj pre pravidlo s host funkciou; preto `SqlTranslatable` funkcia v `Rule` bez conformance sady **nesmie** vzniknúť (simulátor by mohol rozchádzať interpreter/SQL).

---

## 5. Management API — dopady

### 5.1 Dnešný povrch (main, 10 routes; branch 18)

`info`, `projects`, `descriptor` (GET/PUT), `revisions`, `schema`, `capabilities`, `policy/simulate`, rollback. `/capabilities` už vracia `honoured`/`warned`/`refused` (`CapabilityReport.cs`) — precedens pre introspekciu "čo build vie".

### 5.2 Nové (návrh)

| Route | Účel | Pozn. |
|---|---|---|
| `GET cel/functions` | katalóg funkcií (vstavané + host) | cacheovateľné podľa revízie + hash catalogu |
| `GET cel/scope?entity&profile` | polia, kontext, operátory legálne v profile | generované z `_allowedProfiles` — **jediný zdroj**, nie z UI |
| `POST cel/check` | diagnostika + typ | bez side-effectov, rate-limitovať (je to CPU) |
| `POST cel/evaluate` | evaluácia nad dodanými hodnotami | časový + veľkostný limit; nikdy nečíta dáta |
| `GET capabilities` | rozšíriť o `celFunctions` počet + hash | aby agent vedel, že catalog sa zmenil |

Všetky chránené `ManagementOperations` (existujúci gate `ManagementAccessEndpointFilter`); `check`/`evaluate` na úrovni viewer+ (čítajú len schému), `evaluate` navyše bez `@user` z volajúceho — kontext sa dodáva v tele (inak by operátor videl rozhodnutie podľa vlastnej identity).

---

## 6. Webhooky, CloudEvents, outbox — čo z toho patrí k tejto téme

Spec/analýza určujú (analyza §3.2-3.4): CloudEvents obálka, transactional outbox (Wolverine), Standard Webhooks (hlavičky `webhook-id`, `webhook-timestamp`, `webhook-signature`, HMAC-SHA256 nad id+timestamp+payload — detail špecifikácie som **nepotvrdil** z primárneho zdroja, stránka standardwebhooks.com odkazuje na GitHub spec), retries s backoff+jitter, DLQ, delivery log, redelivery.

Stav v kóde: outbox + dispatcher + after-hook webhook + SSRF egress guard (`WebhookEgressGuard`, `WebhookAddressClassifier`) **sú**; podpis, `secretRef`, retries/DLQ, delivery log, redelivery UI **nie sú** (#120, #33/7.1). Z pohľadu tejto analýzy: dashboard "Integrations editor" (deklarácia endpointu) má zmysel až s `secretRef`; inak by operátor vyplnil pole, ktoré build ignoruje (todo §8a: "warned — not read, no HMAC"). Poradie: endpoint editor **spolu s** #120 alebo s explicitným "unsigned" štítkom.

Porovnanie (analyza §3.1, čítané z repa — externé zdroje som neoveroval samostatne): Directus Flows = benchmark pre builder (blocking Filter vs non-blocking Action, condition vyhodnotená až po štarte → záplava logov; Alvo to rieši "condition je súčasť subscription"); PocketBase = hooky v kóde bez deklaratívnej vrstvy; Supabase = DB webhooks bez condition layer; Hasura/Edge Functions = funkcie ako samostatný runtime (Alvo ich rieši osou `IFunctionRuntime`, nie jazykovým prepínačom). Z toho pre nás: **PocketBase JSVM je blízky analóg csx** (sync, trusted), **Directus builder je analóg ECA UI**, ale ani jeden nemá typovaný výrazový jazyk s registrom funkcií a discovery — tu má Alvo reálnu diferenciáciu.

---

## 7. Bezpečnostné jadro — čo vyžaduje `alvo-security-core-review` (+ `/security-review`)

Všetky body sa týkajú CEL kompilácie / rule enginu / tenancy → PR označiť `needs-deep-review`.

| # | Riziko | Kde | Mitigácia / kontrola |
|---|---|---|---|
| S1 | Host funkcia v `Rule` ⇒ SQL šablóna = host-kontrolovaný SQL text v WHERE | `SqlPredicateRenderer` | šablóna len s `{0..n}` bind-placeholdermi, nikdy konkatenácia; property test rozšíriť (CsCheck) aj na šablóny; refuse v `Rule` do fázy 3 |
| S2 | Funkcia nesmie dosiahnuť tenant/identitu | interpreter | scalar-only podpis; test "funkcia nedostane `AlvoContext`" ako architektonický fact |
| S3 | Delegát hádže / visí v transakcii | `Mutate` in-tx | výnimka ⇒ RFC 7807 + rollback (nie null); rozpočet ms meraný, prekročenie ⇒ fail (spec akceptačné kritérium "before-hook prekročí rozpočet ⇒ rollback"); sync delegát sa nedá prerušiť — priznať |
| S4 | Nedeterminizmus (`Random`, hodiny) ⇒ rozdiel interpreter ↔ SQL, simulátor ↔ produkcia | conformance | `Purity` deklarácia + conformance sada |
| S5 | `cel/evaluate` ako oracle (informačný únik, DoS) | Management | len dodané hodnoty; limity dĺžky (cel: 2000 znakov už je v schéme) a času; rate limit; **nikdy** `@user` z volajúceho |
| S6 | Collation/null-fold divergencia sa môže zopakovať pre string funkcie | `cel.md` residual caveat | každá string funkcia v `SqlTranslatable` musí mať príklad s NULL a prázdnym reťazcom |
| S7 | Lexer hang (#244) je "front door" security core; `cel/check` ho vystaví cez HTTP | `CelLexer.Tokenize` | **#244 blokuje `cel/check`** (inak neautentifikovaný? nie — viewer+, ale DoS je realistický) |
| S8 | csx = RCE pre admina | #34 | mimo rozsahu tohto rezu; UI-edit gate (sekcia 4.5) |
| S9 | `@tenant.id` / `@user.roles` v after-side šablónach (#153) | AfterHook | nemeniť v tomto cykle; funkcie v after-`Condition` ich nedostanú (scalar-only) |
| S10 | Host konvencia môže odstrániť filtre z endpointu (#184) | `DataApiEndpoints.Protect` | relevantné až pre custom endpointy z funkcií (E, http trigger) |

---

## 8. Rozhodnutia pre maintainera

Každé: možnosti, odporúčanie, deviácia od zdroja.

**D1 — Jeden catalog s triedou vyhodnotenia.**
Možnosti: (a) jeden `ICelFunctionCatalog`, vstavané aj host, trieda `InProcess|SqlTranslatable` v podpise; (b) host funkcie bokom (iná cesta ako `lowerAscii`); (c) #272 tak, ako je (jedna registrácia + voliteľná SQL šablóna).
Odporúčanie: **(a)**; vstavané sa presunú do catalogu (jedna cesta v parseri/checkeri).
Deviácia: spec §0 hovorí "nie hotovú knižnicu" — súhlasí; **tvar deklarácie preberáme z cel-go** (typované overloady, docs, examples). Zapísať ako výslovné prevzatie prior art.

**D2 — Čo so `Rule`/`Computed` a "computed at write".**
Možnosti: (a) fáza 1 = len `Condition`+`Mutate`; `Rule`/`Computed` až `SqlTranslatable` v fáze 3; (b) ihneď aj SQL šablóny; (c) nový stupeň "computed at write" (#272 bod 1b).
Odporúčanie: **(a)**; (c) zamietnuť — rebrík computed→rollup→mutate→akcia už pokrýva "vypočítaj pri zápise" cez `mutate` s `$cel` a pridanie stupňa dáva nezaručenú hodnotu (out-of-band zápisy ju obídu; generated column to nerobí).
Deviácia od #272: ten navrhol obe cesty ako rovnocenné; odporúčam odmietnuť (c) a zdôvodniť. Deviácia od analýzy: žiadna.

**D3 — Zdroj pravdy pre editor.**
Možnosti: (a) core endpointy `cel/*` (#273); (b) kompilovať core do WASM pre UI; (c) druhý parser v dashboarde.
Odporúčanie: **(a)**. (b) zdvojí release artefakt, (c) je zakázaná (princíp 7; `RulesTab` to už argumentuje).

**D4 — Editor technológia.** (sekcia 4.2) Odporúčanie **CodeMirror 6 v jednom Blazor komponente**; spike na latenciu interopu pred záväzkom. Deviácia od "Blazor, jeden jazyk" (analyza §2.8): lokalizovaný JS.

**D5 — `automation` blok: vyhodnotiť, alebo zlúčiť s hookmi.**
Možnosti: (a) implementovať ECA evaluator (trigger event/cron, condition, actions) nad existujúcim outboxom; (b) vyhlásiť after-hooky za jediné ECA a `automation` nechať ako F7; (c) odstrániť `automation` zo schémy (breaking, nemožné: v1 je additive-only).
Odporúčanie: **(a)**, ale až po fáze 2 a ako samostatné rozhodnutie (veľké: scheduler, wildcardy #153, loop protection, batch coalescing); dovtedy dashboard "Not yet". Poznámka: #22 DoD ("ECA rule + cron + email work") je CLOSED, kód to nespĺňa — **rozpor stavu s PLAN/issue treba opraviť** (reopen alebo nový issue).

**D6 — Zákaz siete pri host funkciách.**
Spec: "structurally impossible" pre before-hooky. Pri C# delegáte nedosiahnuteľné. Možnosti: (a) deklaratívne + budget + review; (b) vykonať host funkcie out-of-process (neúmerné); (c) nepovoliť host funkcie v in-tx profile.
Odporúčanie: **(a)**, s explicitným zápisom deviácie ("host code is trusted; the guarantee is declared and measured, not structural"), lebo host je embedded vlastník procesu (analyza §2.14: kto vie mountnúť, vlastní kontajner).

**D7 — Typový systém: `Date`, `Decimal` precision.**
#272 príklad používa `DateOnly`; `CelValueType` ho nemá. Možnosti: len existujúce typy (Timestamp); pridať `Date`. Odporúčanie: začať existujúcimi (Timestamp), `Date` ako samostatný additívny PR s dopadom na schému/DDL; nezamiešať do #272.

**Otvorené otázky (neviem):**
* Aký je zámer pre `IFieldFormatValidator` (v schéme spomenutý, v `src` nenájdený)? Má zdieľať catalog/discovery?
* Správanie výnimky z delegátu v interpreteri: fail-closed (zápis zlyhá) vs null-collapse. Doktrína dnes: interpreter nikdy nehádže (null), ale to platí pre vstavané čisté funkcie.
* Či `Access` profil smie volať host funkcie (len `@user`); návrh: nie vo fáze 1.
* Reálna cena `cel/check` na Blazor Server circuit (latencia) — spike.
* Presný text Standard Webhooks (headers/signing) — nenačítaný z primárneho zdroja.
* Ako sa catalog hostov zdieľa v standalone móde (Docker image bez kódu hostu): vstavané funkcie áno, host nie — treba povedať v dokumentácii; csx funkcie by mohli byť jediný extensibility bod v standalone, ale nie sú výrazy.

---

## 9. Rozdelenie na PR / issues

### 9.1 Mapa závislostí a prekryvov

| Issue | Čo to je | Vzťah |
|---|---|---|
| #272 | host CEL funkcie | **jadro**; blokuje host-časť #273 |
| #273 | expression editor + evaluator | závisí od `cel/*` endpointov (nový), čiastočne od #272, blokovaný #244 |
| #276 | hooky end-to-end v dashboarde | používa `<ExpressionEditor>` z #273; endpoint/template picker závisí od #271 (formats/panel) a endpoint editor od #120 |
| #28 | rules/automation builder + simulátor + csx editor | **prekrýva #273 + #276 + #34**; rules builder+simulátor hotové vo vetve, zvyšok je rozpad (viď nižšie) |
| #33 | Automation extension (HMAC, retries+DLQ, redelivery, inbound webhook) | **prekrýva #120** (HMAC/DLQ) — duplikát rozhodnutia o doručovaní |
| #120 | endpointy deklarované, nedoručované | podmnožina #33 |
| #152 | projekcia per endpoint | nezávislé, ale "spolu s #120" (privacy payload) |
| #149 | JSONata evaluátor | blokuje `webhook.payload` ako JSONata; nezávislé od CEL katalógu; **architektonický dôkaz zákazu v tx** |
| #34 | csx scripting | trust trieda E; nezávislé od F; blokuje csx editor |
| #35 | `IFunctionRuntime` | nad #34; blokuje trigger HTTP/event/schedule funkcií |
| #44 | descriptor templates (std knižnica) | **iný pojem než `templates` (message)**; v návrhu nie je vlastníkom rules/hookov (analyza §10.4: len entity+polia+vzťahy) — **mimo tejto práce**, len disambiguovať názov |
| #153 | `@tenant`/`@user.roles` v šablónach | brzdí wildcard v `automation.trigger`; nie v tejto práci |
| #113 | `field.default` CEL polovica | patrí do rodiny "výraz v hodnotovej pozícii" — po D2 sa dá vyriešiť pomocou `Mutate`-like profilu pri insert; **neriešiť teraz** |
| #82 | `parameterPrefix` overload + filter breadth | netýka sa priamo; ak sa mení `IPredicateRenderer` (šablóny), spojiť |
| #85 | uzavrieť `CelNode` | **predpoklad** pre zmenu `CelCall` na N-árne volanie |
| #244 | lexer bez "position must advance" | **predpoklad** pre `cel/check` |
| #287 | `has()` guards v Computed | ovplyvní, čo `cel/check` hlási ako falošné odmietnutie; nezávislé |
| #291 | mapper throws vs validator | ovplyvní `check_change` asistenta; nezávislé, ale patrí do rodiny "fail-fast = štruktúrovaná chyba" |
| #103 | runtime apply nepridá route | blokuje HTTP trigger funkcií pri runtime apply; netýka sa expression práce |
| #184 | "marked endpoint is a gated endpoint" | až pre custom endpointy |

### 9.2 Návrh PR (poradie)

| Vlna | PR | Obsah | Predpoklady | Veľkosť |
|---|---|---|---|---|
| 0 | **P0a** | #244 (lexer invariant) + #85 (uzavrieť `CelNode`) | — | S |
| 0 | **P0b** | `CelCompilationError` + dĺžka spanu; `CelProfile`→scope introspekcia (`ICelScope`) | — | S |
| 1 | **P1** | `ICelFunctionCatalog` + vstavané (`lowerAscii`, `now`) presunuté + N-árne `CelCall` + `InProcess` len pre `Condition`/`Mutate` + `AddCelFunction` + fail-fast + conformance príklady | P0a, D1, D6 | L, **security-core deep review** |
| 1 | **P2** | Management: `cel/functions`, `cel/scope`, `cel/check`, `cel/evaluate` + `capabilities` hash; OpenAPI mimo dokumentu ako ostatné management routes | P0, P1 | M |
| 2 | **P3** | Dashboard `<ExpressionEditor>` (CodeMirror, spike → komponent) nahrádza 6 `MudTextField`; try-it panel | P2, D4 | L |
| 2 | **P4** | #276: hook edit-in-place, mutate literal/multi-field, pickery, endpoint/template deklarácia v Integrations (s "unsigned" štítkom, kým #120) | P3, #271 | M–L |
| 2 | **P5** | AI skills: generovaná sekcia funkcií (`alvo-descriptor-rules-and-cel`, `-hooks`) z catalogu + eval prípad | P1 | S |
| 3 | **P6** | `SqlTranslatable`: `DialectId`, šablóny, rozšírený property test, `CelFunctionConformance` v `MMLib.Alvo.Testing`, povolenie `Rule`/`Computed` | P1, D2 | L, **deep review** |
| 3 | **P7** | #120/#33: Standard Webhooks signing + `secretRef` + retries/DLQ + delivery log; #152 rozhodnutie | nezávislé od P1–P6 | L |
| 4 | **P8** | D5(a): ECA evaluator → až potom Automation builder (#28 zvyšok) | P4, P7, D5 | XL |
| 4 | **P9** | csx (#34) + `IFunctionRuntime` (#35) + csx editor s UI-edit gate | samostatná analýza; trust S8 | XL |

### 9.3 Čo zlúčiť/zavrieť/re-scopeovať

* **#273** — ponechať, re-scope: "ExpressionEditor + `cel/*` endpointy" (P2+P3); host funkcie ako závislosť na #272.
* **#272** — ponechať, **prepísať** v zmysle D1/D2 (jeden catalog, fáza 1 InProcess; zamietnutý "computed at write" s odkazom na rebrík).
* **#28** — **rozbiť**: rules builder + simulátor je hotové (vetva); csx editor → #34; automation builder → po D5; webhook delivery log → #33. Inak ostane "ready, needs-deep-review" ticket, ktorý nikto nedokáže uzavrieť.
* **#33 vs #120** — zlúčiť: #120 je podmnožina #33, rozhodnutie o doručovaní je jedno.
* **#276** — ponechať; závislosť na P3.
* **#44** — pridať do titulku "(descriptor fragment library)" a poznámku, že **nie je** `templates` (message) ani `functions`.
* **#22** — pri rozhodnutí D5 doplniť komentár (DoD nesplnené v časti ECA).

### 9.4 Riziká

| Riziko | Pravdepodobnosť | Dopad | Reakcia |
|---|---|---|---|
| Zmena `CelCall` rozbije walkers | stredná | vysoký (security core) | #85 vopred; golden + differential testy zostávajú |
| Vetvy `f5/admin-dashboard` a `main` sa rozišli (CEL sa upravovalo na oboch) | vysoká | stredný | P3 začať **po** merge vetvy do `main`; P1 robiť proti `main` |
| Host funkcia zmení správanie medzi verziami hostu a zmení stored hodnotu | stredná | stredný (Computed) | `Version` v deskriptore + hash v `capabilities`; fáza 3 |
| CodeMirror interop pomalý na Blazor Server | neznáma | stredný | spike, fallback C |
| `cel/check` ako DoS | stredná | stredný | #244, limity, rate limit |
| Dvojitý význam "template"/"function" | vysoká | nízky–stredný (mätie agenta) | názvoslovie v skills a docs |

### 9.5 Numerické akceptačné kritériá (z baas-analyza; kde analýza neuvádza číslo, písím návrh)

Z analýzy (citované):
* Pravidlo odkazujúce na neexistujúci stĺpec zlyhá **pri uložení**, nie pri requeste (§2.4) → analogicky: neznáma funkcia / zlá arita / zlý typ = chyba pri apply, 100 % prípadov v teste.
* Property-based testy dokazujú, že preklad pravidla → SQL **nikdy neinterpoluje** užívateľský vstup (§2.4) → rozšíriť na šablóny funkcií (P6).
* Policy simulátor odpovedá **identicky** ako produkčné vynucovanie pre každú kombináciu (user, operácia, riadok) (§2.4).
* Podmienka `changed(status) && new.status == 'approved'` spustí **práve raz** pri prechode (§3).
* Blocking before-hook prekročí rozpočet ⇒ čistý rollback s RFC 7807 (§3) → pre host funkciu: test s delegátom, ktorý spí.
* Dashboard použiteľný na **375 px** bez horizontálneho scrollu (§2.8) → editor výrazov musí tento test prejsť (existuje e2e fact z todo §5f).
* AI-navrhnutá zmena sa zobrazí ako diff a aplikuje po potvrdení (§2.8) → platí aj pre návrh s host funkciou.
* Webhook: konzument s referenčnou Standard Webhooks knižnicou verifikuje podpis; replay so starým timestampom odmietnutý; endpoint down 2 h ⇒ doručenie v poradí per key; 10k bulk = 1 batch event (§3.4) → P7.

Návrh (analýza čísla nemá): `cel/check` p95 < 50 ms pre výraz ≤ 2000 znakov na SQLite; debounce 300 ms; `cel/evaluate` hard timeout 250 ms. Existujúca infraštruktúra: `docs/performance.md`, `scripts/test-load`, #188 (micro-benchmarky CEL compile) — **číslo treba zmerať, nie vymyslieť**; navrhujem ho pripnúť do #188.

---

## 10. Čo sa stane s existujúcimi dokumentmi (keď sa rozhodne)

Podľa pravidla "sweep for the claim, not the file": po rozhodnutí D1/D2 treba grepnúť **text tvrdenia** v: `docs/architecture/cel.md` (tabuľka profilov, "exactly two entries"), `schema/project.schema.json` (popis `cel`, `functions`), `.claude/skills/alvo-descriptor-*` (rules-and-cel, hooks, computed-and-rollups, capabilities-and-limits), `docs/todo-admin.md` (položky 33, 34), `docs/design-brief.en.md` (regenerovať cez `alvo-regen-brief`, ak sa dotkne spec/analýzy), `UnhonouredFeatures` fix-texty. Spec §0 princíp 6 ("preberáme spec a syntax, nie knižnicu") a "CEL … `@user/@tenant`" riadok vyžaduje poznámku o rozšírení slovníka funkcií.

---

## 11. Zdroje

### 11.1 Repo (čítané)

* `CLAUDE.md` (pracovné dohody), `docs/PLAN.md` (§3 "YOU ARE HERE" F5)
* `docs/design-brief.en.md` (grep: 44-54, 78-91, 124-190, 335-350, 396-422)
* `docs/product/baas-analyza.md`: §2.4 (243-278), §2.7 (341-392), §2.8 (393-431), §3 (588-688), §10 (915-953)
* `docs/product/alvo-specifikacia.md`: §0 (10-31), 54-57, 93, 135, 209-235, 286-312, 328-334, 349, 373-395
* `docs/architecture/cel.md`: 15-70 (tabuľka profilov), 115-165 (allow-list), 296-360 (`IFieldSqlRenderer`), 440-474 (odchýlky)
* `schema/project.schema.json`: `$defs/cel`, `celExpr`, `valueOrExpr`, `jsonata`, `beforeHookList`, `afterHookList`, `automationRule`, `action`, `template`; blok `functions` (249+)
* `src/MMLib.Alvo.Abstractions/Expressions/{ICelCompiler,CelProfile,CompiledExpression,CelCompilationResult,CelNode,CelValueType,IFieldSqlRenderer}.cs`; `Rules/IBeforeHookRunner.cs`; `Events/IAlvoEvents.cs`; `IAlvoBuilder.cs`
* `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs:100-160,690-720`, `CelParser.cs:417-460`, `CelInterpreter.cs:189-266`, `SqlPredicateRenderer.cs:120-160`, `CelTree.cs:63`
* `src/MMLib.Alvo/Events/Internal/{ActionVocabulary,AfterHookCompiler,JsonataSlot}.cs`; `Descriptor/Internal/{UnhonouredFeatures,UnhonouredSubsystems}.cs`; `Management/Internal/{ManagementEndpoints,CapabilityReport}.cs`
* `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ComputedColumnSql.cs` (len existencia)
* Dashboard (`origin/f5/admin-dashboard`): `docs/todo-admin.md` (§3, §5, §7, §8, položky 14, 20, 26, 33, 34), `Components/Schema/{HookBuilder.cs,HooksTab.razor:123-214,FieldEditor.razor:246}`, `Components/Rules/RulesTab.razor:49,79`, `Internal/ManagementGateway.cs`, `src/MMLib.Alvo.Ai/Internal/SkillAreas.cs`, `.claude/skills/alvo-descriptor-*`
* GitHub issues: #22, #28, #33, #34, #35, #37, #44, #82, #85, #103, #113, #120, #149, #152, #153, #184, #244, #272, #273, #276, #287, #291 (a zoznam otvorených súvisiacich: #213, #229, #252, #188, #154)

### 11.2 Externé (reálne načítané týmto behom)

* CEL language definition (extension functions, overloads, determinism, termination, cost): https://github.com/google/cel-spec/blob/master/doc/langdef.md
* cel-go custom functions (`cel.Function`, `cel.Overload`, bindings, cost estimation, `FunctionDocs`): https://pkg.go.dev/github.com/google/cel-go/cel ; https://celbyexample.com/custom-functions/
* cel-net (`RegisterFunction` pred parse): https://github.com/telus-labs/cel-net ; NuGet https://www.nuget.org/packages/Cel/
* PostgreSQL generated columns (immutable-only; virtual: len vstavané): https://www.postgresql.org/docs/current/ddl-generated-columns.html
* CodeMirror 6 autocomplete/lint (async `CompletionSource`, `Diagnostic{from,to}`): https://codemirror.net/docs/ref/#autocomplete
* JSONata je Turing-complete (rekurzia, TCO): https://docs.jsonata.org/programming
* Standard Webhooks (iba úvodná stránka; spec na GitHub, nečítané): https://www.standardwebhooks.com/
* Roslyn scripting API samples — **nepotvrdzuje** žiadny sandbox (stránka rieši len `ScriptOptions.WithReferences/WithImports`): https://github.com/dotnet/roslyn/blob/main/docs/wiki/Scripting-API-Samples.md ; trust model csx teda stojí výlučne na rozhodnutí v baas-analyza §2.7, nie na vlastnosti Roslynu.

### 11.3 Neprečítané / spoliehané na repo (poctivo)

Wolverine/outbox interná implementácia, CloudEvents spec priamo, Directus/Hasura/PocketBase/Supabase primárne dokumentácie (použité len porovnanie z baas-analyza §3.1 a §10.2), Monaco/Blazor integrácie, Postgres RLS dokumentácia priamo (použitý popis z baas-analyza §2.4), samotné telo `docs/superpowers/specs/2026-08-02-f3-pr5-events-hooks-design-addendum.md` (98 kB) a `…pr6-computed-rollup-design.md` — tieto dva by mal P1 prečítať pred návrhom (obsahujú číslované deviácie, na ktoré `cel.md` odkazuje, napr. "deviation 7, 8, 79").

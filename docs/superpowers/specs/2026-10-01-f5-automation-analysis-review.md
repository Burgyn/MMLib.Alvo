# Adversarial review: automation-analysis.md

Reviewer: skeptický senior architect + product thinker. Read-only. Fakty overené proti `main` @ `ee0a6d4` a `origin/f5/admin-dashboard`.

## 0. Celkový verdikt

Analýza je technicky poctivá (fakty z kódu sedia, otvorené otázky sú pomenované), ale **je to analýza z pohľadu implementátora, nie používateľa**. Kostru tvorí "ako správne postaviť catalog + SQL/in-process triedy + CodeMirror editor", a nikde sa nepýta, kto a ako často bude tieto veci naozaj používať. Tri hlavné výhrady:

1. **Over-engineering fázy 1.** `ICelFunctionCatalog` s `Purity`, `Cost`, `Provenance`, `Examples`, `Remarks`, overloadmi a `CelFunctionConformance` je tvar cel-go pre knižnicu s tisíckami konzumentov. Alvo má dnes jedného (maintainera) a persona (1) potrebuje: `builder.AddCelFunction("normalizePhone", (string s) => ...)`. Overloady, `Cost` a `Purity` sú vo fáze 1 mŕtva váha.
2. **Under-serving persony (2) a (3).** Primárny UX pre operátora, ktorý nie je CEL expert, je odporúčaný CodeMirror editor (D4). To je nástroj pre vývojára. Operátor chce "keď sa stav zmení na X, pošli e-mail Y" ako formulár. Hooky v dashboarde už takmer sú guided formulár (`HookBuilder`), lenže mu chýba práve tá časť, ktorú analýza odsúva (picker endpointov, šablón, literál mutate).
3. **Headline "automation" ostáva prázdny.** Wave 4 (XL, bez dátumu) je jediné miesto, kde `automation` začne fungovať, a medzitým sa v P3 investuje najväčší kus UI práce do CEL editora. Poradie hodnoty je obrátené: najprv to, čo operátor vidí a pochopí, potom power-tool.

## 1. Verdikt k D1–D5 (+D6, D7)

| D | Verdikt | Dôvod |
|---|---|---|
| **D1** jeden catalog + trieda vyhodnotenia | **MODIFY (zúžiť)** | Jeden zdroj pravdy pre checker/interpreter/API/AI je správny (a presun `lowerAscii`/`now` do neho je dobrý, dnes sú natvrdo v `CelCall` + parseri). Ale vo fáze 1 ho dodať ako *minimum*: `Name`, jedna signatúra (typy parametrov + výsledok), `Summary`, `Provenance`. `Purity`, `Cost`, `Examples`, overloady a `Evaluation` enum pridať, až keď existuje druhá hodnota enumu (`SqlTranslatable`). Verejné API sa zle sťahuje späť; YAGNI tu stojí reálne peniaze (Abstractions je frozen). Pozor: `Evaluation` v podpise vo fáze 1 s jedinou hodnotou je zbytočný zámok. |
| **D2** zákaz in-process v `Rule`/`Computed`, zamietnuť "computed at write" | **KEEP časť (a), FLIP časť (c)** | Zákaz in-process vo `Rule` je správny a vyplýva zo špecifikácie (autorizácia v SQL WHERE). Zamietnutie "computed at write" je však **produktovo naivné**: persona (1) s `vat_rate`/`normalizePhone` chce *uloženú* hodnotu pri zápise (cena s DPH, normalizovaný telefón na unikátny index/filter). `mutate` s `$cel` to robí, ale iba v hooku a iba ako `$cel`; analýza sama priznáva, že dashboard nevie `mutate` literál ani multi-field. Ak sa nepridá výslovný "stored computed" ekvivalent, host developer použije `mutate` v hooku, čo je OK, ale **to treba povedať ako oficiálny recept, nie zamietnuť issue**. Argument "out-of-band zápisy ju obídu" platí rovnako pre `mutate`. Preformulovať: nie "nepridávať stupeň", ale "recept = before-hook mutate; ak sa ukáže, že je to rozšírený vzor, zvážiť `field.default`/`computed.stored` (#113)". |
| **D3** zdroj pravdy `cel/*` endpointy | **KEEP, ale rozsah zmenšiť** | Žiadny druhý parser v UI: správne. Ale z štyroch endpointov (`functions`, `scope`, `check`, `cel/evaluate`) je `evaluate` najdrahší (DoS, oracle, `@user` kontext, limity) a pre cieľovú personu najmenej potrebný. `check` + `functions` stačia na hodnotu. `evaluate` je už čiastočne pokrytý `policy/simulate` pre Rule. |
| **D4** CodeMirror 6 | **FLIP (ako primárny UX)** | Pozri sekciu 3. CodeMirror je dobrý *sekundárny* "advanced" režim. Primárne má byť guided formulár (pole z dropdownu, operátor, hodnota) s prepnutím na textový výraz, a AI "opíš slovami". Spike latencie interopu je nutný, ale nie je to ten hlavný dôvod. |
| **D5** automation (a) po fáze 2 | **MODIFY** | Smer (a) je správny, ale "po fáze 2" a XL bez rozseknutia je prázdny sľub. Odporúčam rozseknúť: (5a) `automation` s **jediným** triggerom `event` + condition + actions `webhook/email` = v podstate "after-hook, ale deklarovaný mimo entity" (lacné: re-use `AfterHookCompiler`); (5b) cron/scheduler, wildcardy, batch coalescing, loop protection neskôr. Plus: zvážiť, či `automation` nemá byť **iba pomenovaný alias/zoznam after-hookov** (D5 variant b), kým nepríde cron. Dva paralelné spôsoby, ako povedať to isté, je pre agenta (persona 3) horšie než jeden. |
| **D6** zákaz siete deklaratívne | **KEEP** | Poctivé. Doplniť meranie v PR (test so spiacim delegátom) a explicitný timeout, ktorý nezávisí od kooperácie delegáta (aspoň `CancellationToken` v kontexte, aj keď scalar-only). |
| **D7** `Date` | **KEEP, ale pozor na dôsledok** | Overené: `CelValueType` Date nemá. Ale to znamená, že persona (1) s dátumom (`vat_rate(country, kind, date)`) dostane `Timestamp` a musí riešiť časové pásmo. Je to reálna bolesť a treba to zapísať ako známe obmedzenie v docs funkcie. |

## 2. Pokrytie reálnych potrieb: čo dizajn NEpokrýva (zoradené podľa závažnosti)

| # | Medzera | Závažnosť | Komentár |
|---|---|---|---|
| G1 | **Async / I/O funkcie** (lookup ceny z DB, volanie doménovej služby) | **VYSOKÁ** | Persona (1) prvý príklad "lookup a price, call a domain service" je presne to, čo `InProcess` sync scalar-only delegát nesmie. Analýza to odkazuje na "after-akciu alebo csx", čo je odpoveď "nie". Reálna odpoveď musí existovať: *after-hook akcia typu host-defined* (C# `IAfterHookAction`), nie funkcia vo výraze. Jej absencia znamená, že host developer nemá žiadnu cestu pre doménovú logiku s I/O. csx je odložené (XL), `function`/`http.call` action je *refused*. **Celý host-extensibility príbeh je teda iba čisté skalárne funkcie.** |
| G2 | **Tenant-aware / user-aware funkcie** (`vat_rate` per tenant, `maskFor(@user.role)`) | **VYSOKÁ** | Scalar-only ako "štrukturálna ochrana" je pekný argument pre security, ale produktovo to znamená, že funkcia nikdy nevie tenant. Riešenie "pošli `@tenant.id` ako argument" funguje pre `Mutate`/`Condition` (kde `@tenant` je dostupné), len to treba vo výraze *napísať* a agent to musí vedieť. Doplniť do skillu. Ak host chce DI-scoped konfiguráciu per tenant, nemá ako. |
| G3 | **DI scope / lifetime** | **VYSOKÁ** | Analýza o DI mlčí (iba "closure môže zachytiť čokoľvek"). `AddCelFunction(lambda)` je singleton, takže funkcia nemôže využiť scoped `DbContext`/`IPriceService`. Treba rozhodnúť: singleton-only (a povedať), alebo `IServiceProvider`-resolved typed class (`ICelFunction<TIn,TOut>`) popri lambdách. Teraz je to nezodpovedaná otázka, ktorú persona (1) narazí ako prvú. |
| G4 | **Verziovanie funkcií a breaking change** | **STREDNÁ-VYSOKÁ** | Zmenená sémantika `vat_rate` mení uložené `mutate` výsledky. Analýza spomína "Version v deskriptore + hash" iba pre Computed vo fáze 3. Chýba pravidlo: funkcia má `Name` + (voliteľne) `Since`/`Version`, descriptor sa viaže na meno; zmena sémantiky = nové meno (`vat_rate_v2`). Treba aspoň zapísať konvenciu, aby sa agent neprekvapil. |
| G5 | **Prenositeľnosť descriptoru, chýbajúca funkcia** | **STREDNÁ** | Analýza to rieši správne (apply zlyhá s "unknown function"). Chýba: **štruktúrovaná chyba musí nesú zoznam registrovaných funkcií a najbližšiu zhodu** (typo `normalisePhone`) a `/capabilities` musí byť čitateľné *pred* apply (agent si to overí). Nie je to v P1 acceptance. |
| G6 | **Testovanie funkcie** | **STREDNÁ** | Hlavne `CelFunctionConformance` (fáza 3, iba SQL). Persona (1) potrebuje v fáze 1 jednu vec: `AlvoTest.Cel("normalizePhone(new.phone)", ...)` v `MMLib.Alvo.Testing`, alebo aspoň `Examples` ako spustiteľný unit test. `Examples` v podpise je dobrý nápad, ale bez konzumenta je to dokumentácia. |
| G7 | **Sémantika chýb** (výnimka z delegáta) | **STREDNÁ** | Analýza to nechala ako otvorenú otázku. Nesmie ostať otvorená pri merge P1: je to správanie, nie detail. Odporúčanie: výnimka = RFC 7807 + rollback; `null` vstup = funkcia dostane `null` iba ak je parameter `T?`, inak výsledok `null` (null-propagation) a toto musí byť v podpise (nullability v `CelParameter`). Analýza o nullability mlčí. |
| G8 | **Discoverability: atribút vs. fluent** | **STREDNÁ** | Iba fluent `AddCelFunction`. Idiomatický .NET host s 15 funkciami chce `[CelFunction("normalizePhone")] static string Normalize(string s)` + `AddCelFunctionsFrom(assembly)` alebo source generator. Nie je to fáza 1, ale **podpis registrácie musí byť navrhnutý tak, aby sa atribút dal doplniť bez zmeny catalogu** (atribút = len iný zdroj `CelFunctionDescriptor`). Pridať ako požiadavku. |
| G9 | **Secrets / config prístup** | **STREDNÁ** | Funkcia (napr. VAT tabuľka) potrebuje config. Rieši G3 (DI). Pre standalone neexistuje. |
| G10 | **i18n** | **NÍZKA-STREDNÁ** | `Summary`/`Remarks` iba v jednom jazyku; chybové hlásky z `CelCompilationError` sú EN. Pre dashboard so slovenským operátorom to zaváži, ale agent číta EN, takže OK. Nechať otvorené, iba nepísať do podpisu pevné `string Summary` bez možnosti kľúča. |
| G11 | **Determinizmus / caching / cost limity** | **NÍZKA vo fáze 1** | `Cost`, `Purity` bez konzumenta. Cacheovanie výsledkov funkcie (napr. `vat_rate`) je skôr otázka implementácie funkcie. Nechať fáze 3. |
| G12 | **Docs pre AI** | **STREDNÁ** | P5 je správny (generovaná sekcia), ale je pozdĺž P1 v 2. vlne. Agent bez toho vlastne o funkcii nevie: `/capabilities`/`cel/functions` musia byť dostupné ihneď s P1, nie až s P2. |

## 3. Persony

**(1) Host developer.** Chce: `services.AddAlvo().AddCelFunction(...)` a použiť v computed/rule/hook/mutate. Dostane: Condition + Mutate. *Computed a Rule sú práve tie dve miesta, kde to chce prvý.* (Normalizovaný telefón v `computed`, filter podľa neho.) Fáza 1 mu teda dá polovicu a ďalšia polovica je L-sized fáza 3 s conformance. **Návrh:** zvážiť *host-vlastnú* trojicu "delegát + SQL šablóna" od začiatku, ale **len pre jeden dialekt-neutrálny prípad** (reťazcové a aritmetické funkcie zložené z už existujúcich vstavaných operácií, t.j. makro-rozšírenie na úrovni AST, nie SQL text). Napr. `normalizePhone(x)` = `replace(replace(x, ' ', ''), '-', '')` ako *CEL výraz hosta*. Takáto **"macro function"** (host definuje funkciu ako CEL výraz nad parametrami) je:
 - automaticky SQL-prekladateľná na všetkých enginoch (žiadne šablóny, žiadna conformance, žiadny SQL text od hosta, žiadny S1 riziko),
 - bezpečná (rovnaká gramatika),
 - pokrýva väčšinu "prepočítaj/normalizuj/zaokrúhli" prípadov.
 Táto *tretia trieda* (`Macro`) v analýze chýba úplne a je lacnejšia než `SqlTranslatable` so šablónami. Ale vyžaduje, aby CEL mal dosť primitív (dnes iba `lowerAscii` a `now`, takže bez rozšírenia vstavaných funkcií je macro prázdna). To je presne dôvod, prečo je pravdepodobne správnejšie **najprv doplniť tri-štyri vstavané funkcie** (`replace`, `trim`, `substring`/`size`, `round`, `abs`), ktoré pokryjú väčšinu "normalizuj" prípadov, než budovať host-extensibilitu. Analýza tento krok nespomína.

**(2) Operátor, nie expert na CEL.** CodeMirror s completion a diagnostikou mu pomôže, ale stále píše `new.status == 'approved' && changed(status)`. Prior art, ktoré poznajú: Directus Flows (karta Condition: pole/operátor/hodnota), Zapier ("Filter: if X contains Y"), Airtable automations. Žiaden z nich nezačína textovým výrazom. Analýza porovnáva tieto nástroje, ale vyvodzuje z nich závery o *funkčnosti*, nie o *UX*. **Moje odporúčanie:** primárny UX = guided `when [pole] [je/nie je/obsahuje/zmenilo sa na] [hodnota]` generujúci CEL, s odkazom "Show expression" na textový režim. Ak sa výraz nedá spätne rozložiť do formulára, zobraziť ho iba v textovom režime. A **AI "opíš slovami" je pre túto personu hodnotnejšia ako editor**, lebo pokrýva aj `mutate`/`reject`/endpoint výber jednou vetou; AI asistent už vo vetve je (`MMLib.Alvo.Ai`).

**(3) Agent / AI asistent.** Funkcie musia byť objaviteľné pred apply, s príkladmi, a chyba pri apply musí navrhnúť opravu. Analýza to kryje dobre (P5, fix suggestions), ale P5 je v 2. vlne a žiadny agent nebude vedieť o hostovských funkciách, kým nie je P2. **Posunúť `cel/functions` + capabilities hash do P1.** Ďalšie: agent ťažko zvládne SQL šablóny/dialekty (fáza 3); macro funkcie sú pre neho jednoduchšie.

**(4) Koncový zákazník host aplikácie.** Z analýzy takmer úplne vypadol. Dotkne sa ho iba chyba z `reject`/`mutate` (RFC 7807) — hlásenie musí byť lokalizovateľné/hostom prepísateľné (G10) a nesmie prezradiť názvy funkcií/stĺpcov z rule. Tvrdenie o tom v analýze chýba.

## 4. Prior art: čo analýza nevyvodila

* **PocketBase hooks / Supabase triggers:** používateľ napíše kód v hostiteľskom jazyku, ktorý beží s plným prístupom. Alvo sa rozhodlo pre deklaratívne, čo je dobrý edge, ale *únikový ventil* (kód v C#) je nutný, inak sa používateľ obráti na vlastný middleware. Analýza ten ventil odkladá (csx = XL). Lacnejšia cesta: **`IBeforeHook`/`IAfterHook` C# tvár** (spec ju sľubuje, "dve tváre … cez jeden pipeline"), ktorá už ako port existuje (`IBeforeHookRunner`), ale bez builder API. Pridanie `builder.AddBeforeHook(...)` je lacnejšie než csx a pokryje G1.
* **Hasura actions / event triggers:** action = HTTP na vlastný server; event trigger = webhook. Alvo after-hook `webhook` = event trigger. **Hasura "action" (synchrónne volanie vlastného endpointu pred zápisom) Alvo úmyselne nemá** (zákaz siete v tx). Správne, ale to je prvá otázka, ktorú Hasura používateľ položí. Mala by byť v docs ako "prečo nie".
* **cel-go:** tvar je OK, ale cel-go má stovky konzumentov a cost model, ktorý riadi vlastné vyhodnotenie. Alvo prebrať iba `Name + typed overload + doc`.
* **Directus Flows:** builder s kartami je hlavný produkt. Alvo ho odkladá (P8). Zdôrazňujem: Directus nikdy nespočíval na CEL-editore.

## 5. Slicing 9 PR / 5 vĺn: dodateľnosť a poradie

Problémy:

1. **P0a bundluje #244 + #85.** #85 (uzavrieť `CelNode`) je API-breaking zmena (overené: `CelNode` je `public abstract record`, `CelCall` internal). Nemá to byť S. Mala by ísť samostatne s approval diffom `PublicApi.*.verified.txt`.
2. **P3 (L) pred P4 (M–L):** poradie hodnoty je obrátené. P4 (#276: hooky end-to-end) dáva operátorovi použiteľný výsledok *bez* nového API; P3 je investícia do power-toolu. Zámena: **P4 pred P3**, a P3 s menším rozsahom (textarea + `cel/check` diagnostika, bez CodeMirror).
3. **P2 závisí od P1 iba pre `cel/functions`.** `cel/check` a `cel/scope` nezávisia od funkcií vôbec a môžu ísť *pred* P1. Tým sa dashboardu dá diagnostika hneď.
4. **P5 (AI skills) je S a má najvyšší pomer hodnota/cena** pre persony 2 a 3, ale stojí na P1. Ak sa P1 zúži (viď D1), P5 sa dá doručiť rýchlejšie.
5. **P7 (#120/#33 podpis + secretRef + retries/DLQ) je nezávislý a pravdepodobne najviac hodnoty pre skutočných zákazníkov** (nepodpísané webhooky sú bezpečnostná medzera, ktorú žiadny integrátor neprijme). Je v 3. vlne za L-sized fázou 3 SQL šablón (P6), ktorú takmer nikto nepotrebuje (persona 1 by sa uspokojila s macro/vstavanými). **Posunúť P7 pred P6, a P6 odložiť do doby, kým reálny host požiada.**
6. **P8 a P9 sú XL bez rozseknutia.** Hlavný sľub produktu ("automation") je za nimi.
7. Overené riziko "vetvy sa rozišli": P1 proti `main` a P3 po merge dashboardu je správne.

## 6. Fact-check 12 najdôležitejších tvrdení

| # | Tvrdenie | Stav | Dôkaz |
|---|---|---|---|
| 1 | `automation` a `functions` sú v `UnhonouredSubsystems` na riadkoch 127, 145 | **CONFIRMED** | `UnhonouredSubsystems.cs`: `"automation"` ("no rule is ever evaluated"), `"functions"` ("no function is ever invoked"); presné čísla riadkov sa mierne líšia (čítané ~125/143), obsah sedí |
| 2 | `CelCall` je internal, jediný argument `CelCall(string Name, CelNode? Argument)` | **CONFIRMED** | `CelTree.cs:63` `internal sealed record CelCall(string Name, CelNode? Argument) : CelNode`; parser (`CelParser.cs:455`) ho navyše vyrába iba s `field` operandom, teda argument je ešte užší než "ľubovoľný výraz" |
| 3 | `CelNode` je `public abstract record` | **CONFIRMED** | `Abstractions/Expressions/CelNode.cs:15` |
| 4 | Žiadny `Date` typ v `CelValueType` | **CONFIRMED** | enum: Bool, Int, Decimal, String, Timestamp, Uuid, Json, StringList, Null |
| 5 | Vstavané sú iba `lowerAscii` a `now()` | **CONFIRMED** | `CelParser.cs:455,476`, `CelTree.cs` konštanty; `CelTypeChecker` mapuje `CelConstructKind.Call` na `_mutateOnly` (ľahko zavádzajúce: `Call` je povolený iba v profile `Mutate`) |
| 6 | `_sqlRenderedProfiles = {Rule, Computed}` | **CONFIRMED** | `CelTypeChecker.cs:154-155` |
| 7 | `function`, `http.call`, `entity.update` akcie sú refused | **CONFIRMED** | `UnhonouredFeatures.cs:242-243` `EveryActionType = [FunctionType, HttpCallType, EntityUpdateType]` + fix texty |
| 8 | Dashboard: každý CEL vstup je `MudTextField` (HooksTab:123, RulesTab:79, FieldEditor:246) | **CONFIRMED** | `HooksTab.razor:123` (`hook-condition`), `RulesTab.razor:79`, `FieldEditor.razor:246` (`new-field-computed`) na `origin/f5/admin-dashboard`; 5+ vstupov (aj `hook-mutate-value`, `new-field-default`) |
| 9 | `src/MMLib.Alvo.Admin` má na main 1 `.cs`, vo vetve 69 `.razor` | **CONFIRMED** | `ls` na main: `AlvoAdminAssets.cs` + csproj; `git ls-tree` vetvy: 69 súborov `.razor` |
| 10 | Management API: 10 routes na main, 18 vo vetve | **CONFIRMED** | `grep Map(Get|Post|Put|Delete)`: 10 na main; `git grep -c` vetvy: 18 |
| 11 | `HookBuilder` zabalí `mutate` vždy do `$cel` | **CONFIRMED** | `HookBuilder.cs` vetva: `[Mutate] = new JsonObject { [MutateField] = new JsonObject { ["$cel"] = MutateValue } }` (jedno pole) |
| 12 | `IAlvoBuilder` má jediný člen `Services`; C# tvár hookov nemá `AddHook` na builderi | **CONFIRMED** | `IAlvoBuilder.cs`; `grep AddBeforeHook|AddHook` v `src` nenašiel nič, existuje len `IBeforeHookRunner` (runtime port). **Pozor:** spec (`alvo-specifikacia.md` "Lifecycle hooks") deklaruje "dve tváre (deklaratívna + C# v embedded hoste)", takže C# tvár je *sľúbená a chýba*, čo analýza spomína len okrajovo |
| 13 (bonus) | `IFieldFormatValidator` "nenájdený v `src`" | **CONFIRMED (len v schéme)** | v zdrojoch nie je typ; spomína ho iba popis schémy (v generovanom XML) a odkazuje na neexistujúci port. To je ďalší sľub bez kódu a patrí do rovnakého registračného vzoru ako G8 |
| 14 (bonus) | `now()` je "viazaný okamih" a Mutate renderer odmieta menom | **UNVERIFIABLE (nečítané do hĺbky)** | `SqlPredicateRenderer.cs:128-160`, `CelInterpreter.cs:189-257` nepreskúmané riadok po riadku |
| 15 (bonus) | #22 CLOSED, ale ECA sa nevyhodnocuje | **UNVERIFIABLE** | stav issue som nevidel (GitHub MCP nedostupný); kódová polovica (nevyhodnocuje sa) je CONFIRMED |

Chyby/spresnenia: čísla riadkov v bode 1 sa líšia o ~2, nie je to zásadné. Žiadne tvrdenie z 12 overených nebolo WRONG. Spoľahlivosť analýzy v faktoch je vysoká; slabá je v úsudku o potrebách.

## 7. Požiadavky, ktoré dizajn musí vedieť neskôr absorbovať (a ako)

| Požiadavka | Ako ju absorbovať bez prerábky |
|---|---|
| Atribútová registrácia `[CelFunction]` | `CelFunctionDescriptor` je dáta; atribút / source generator je len ďalší producent. Podmienka: žiadny zámok na lambde (`Delegate` + `MethodInfo`). |
| DI-scoped funkcie | Invoke cez `Func<IServiceProvider, args, result>` vnútorne; verejné API `AddCelFunction<TService>(...)` neskôr. Preto *nezverejňovať* tvar delegáta, ktorý ho vylučuje. |
| Macro funkcie (CEL výraz hosta) | Nová `Evaluation` hodnota `Macro` s telom ako CEL string; checker ho expanduje. `Evaluation` enum musí byť rozšíriteľný (neuzavretý). |
| SQL šablóny (fáza 3) | Ako navrhnuté (`DialectId`) — dobre navrhnuté, ponechať. |
| Date typ | Additívne v `CelValueType`; funkcie deklarujú `Timestamp` do vtedy. |
| Async / I/O host akcie | Nie ako CEL funkcia: `IAfterHookAction` pre after-hook akciu `{"type":"host", "name": ...}`; descriptor referencuje meno, kód v hoste; rovnaký catalog-vzor a rovnaký fail-fast pri apply. |
| Tenant/user-aware | Explicitný argument (`@tenant.id`), nikdy ambientný kontext. Zapísať ako pravidlo. |
| Versioning | Konvencia mena + `capabilities` hash; descriptor nesie len meno. |
| i18n | `Summary` ako kľúč s default textom. |
| Interpreter ↔ SQL zhoda | Differential test z `Examples` (už v dizajne). |
| `automation` ECA | Nad existujúcim `AfterHookCompiler`; rozseknúť trigger typy. |

## 8. Konkrétne korekcie slicingu

1. Rozdeliť **P0a**: #244 samostatne (S); #85 samostatne s API approval.
2. Presunúť `cel/check` + `cel/scope` pred P1 (nezávisia od funkcií). `cel/functions` s P1.
3. **P4 (#276 hooky) pred P3 (editor)**; P3 začať jednoduchým textovým poľom s diagnostikou z `cel/check`, CodeMirror ako samostatný follow-up po spike.
4. Vyradiť `cel/evaluate` z prvého rezu (DoS/oracle); `policy/simulate` už pokrýva pravidlá.
5. **P7 (podpis webhookov) pred P6 (SQL šablóny).** P6 odložiť, kým to nepožiada reálny host.
6. Pridať **P-new**: pár vstavaných funkcií (`replace`, `trim`, `substring`, `round`/`abs`, `size`) ako trieda `Builtin` s plnou SQL prekladateľnosťou na oboch enginoch (a T-SQL fake). To zaberie väčšinu "normalizuj" prípadov bez host registrácie.
7. Rozseknúť P8: (a) `automation` s event triggerom ako re-use after-hook compilera (M), (b) cron/scheduler (L).
8. C# tvár hookov (`AddBeforeHook`/`AddAfterHook`) ako samostatná lacná položka: pokryje G1 a splní vlastný sľub spec.

## 9. Odporúčanie: čo postaviť ako prvé (najtenší hodnotný vertikálny rez)

**Cieľ rezu:** operátor v dashboarde a agent dokážu autorovať *funkčný* hook bez expertízy v CEL, a host developer dokáže pridať jednu vlastnú skalárnu funkciu.

1. `cel/check` + `cel/scope` + `CelCompilationError` s dĺžkou spanu (P0b, P2-lite). Bez funkcií. Hneď zlepší diagnostiku vo všetkých 5+ poliach (textarea, nie CodeMirror).
2. #276 hooky: edit-in-place, `mutate` literál / viac polí, picker endpointov a šablón z working copy, deklarácia endpointu s "unsigned" štítkom. Guided formulár `when [pole] [op] [hodnota]` pre `condition` (generuje CEL), prepínač na text.
3. Minimálny `ICelFunctionCatalog` (meno, jedna signatúra, summary, provenance), presun `lowerAscii`/`now`, `AddCelFunction` pre `Condition`+`Mutate`, `cel/functions` ihneď s ním, a **generovaná sekcia pre AI skills** (P5) v tom istom PR.
4. 3–5 vstavaných SQL-prekladateľných funkcií (`replace`, `trim`, `round`, `abs`), ktoré pokryjú väčšinu "normalizuj/prepočítaj".
5. Až potom: podpis webhookov (P7), `automation` event-only (P8a), CodeMirror, SQL šablóny, csx.

**Zhrnutie rozdielu oproti analýze:** analýza staví zdola nahor (jadro, endpointy, editor, builder). Ja by som staval zhora nadol od toho, čo operátor skutočne vidí (hooky + guided podmienka + AI), a host-funkcie by som doručil v najúspornejšom tvare, s rezervou na rozšírenie (Macro, DI, atribút), ktorú analýza nechala uzavretú.

# F5 admin — the assistant as a panel, and failures that say what failed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** The schema assistant reads as part of the dashboard — a full-height side panel opened from the header — and a failed turn tells the operator which thing failed (the key, the model, the address) instead of "did not answer".

**Why (spec — the maintainer's report, 24 Sep 2026, driving the bike-workshop demo with an OpenAI key):**
- The launcher is a bright green pill fixed bottom-right; its label ("Ask Alvo" / "Close assistant") is unreadable on it, in the dark theme and as "a strange green" in the light one. The **Ask** button in the drawer has the same problem.
- The drawer "is not a panel but some growing bubble": `.a-assistant` is a floating card bottom-right with `max-height: min(70vh, 640px)` that grows with the thread.
- A turn failed with *"The AI endpoint did not answer…"*. The host log said `HTTP 401 (invalid_api_key)` — the endpoint answered and refused the key. Root cause on that host: `Alvo:Ai:ApiKeySecretRef` was not set, so no key was sent (the OpenAI client sent its `no-api-key-required` placeholder), while Settings showed **connected**.

**Scope:** `src/MMLib.Alvo.Admin` (Assistant, Shell, Settings, `wwwroot/alvo.css`), `src/MMLib.Alvo.Ai` (`AlvoAssistant.cs` failure text), `src/MMLib.Alvo` only for a log warning in `Ai/Internal/AiConnectionResolver.cs`; their tests. **No public API change** in Abstractions (a Settings "key missing" status would need `ManagementInfo` to grow — out of scope, file it).

## Global Constraints

- Tokens only in CSS (`DesignTokenTests`, `ComponentLayerTests`, `StylesheetHygieneTests`, z-index tokens `--z-assistant`); both themes; text on any filled control must meet WCAG AA 4.5:1 in both themes — measure it (compute the contrast of the resolved token pair for light and dark and state the numbers in the report).
- Encoding: `.cs` BOM + CRLF, `.razor` BOM + LF, `alvo.css` no BOM. Methods ≤ ~25 lines, XML docs, comments say why.
- E2E: role / test id selectors only (`EndToEndSelectorTests` ratchet). Keep the existing test ids `assistant-launch`, `assistant-drawer`, `assistant-thread` working (update scenarios that assumed the floating layout, e.g. the bounding-box check in `AssistantScenarios.cs`).
- Never put the provider's own message, the endpoint or the key in the on-screen text (the existing remark on `EndpointFailed` explains why); the status code class is fine.
- Conventional Commits ending with `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`; stage files by name; never push/switch branches/dispatch subagents/touch ports 5080/5090.
- Gates: `dotnet test --project test/MMLib.Alvo.Admin.Tests`, the `MMLib.Alvo.Ai` test project, `scripts/test-admin-e2e`, `scripts/test-prototype`, `scripts/test-ring1`; `dotnet build -c Release` for any project with a `LoggerMessage` change.

---

### Task C (first): The assistant's tools run as the signed-in operator on every step

**Reported:** with a working key, *"ake entity tu mam?"* → the model called `get_schema` and answered that it has no permission to read the schema — for an operator who is `admin`.

**Root cause (controller's reading, verify it):** `ManagementGateway.AsOperatorAsync<T>(Func<IAsyncEnumerable<T>>, …)` (`src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs` ~331) sets `ambient.Principal` once at the top of an **async iterator** and then `await foreach`es the inner stream. `IAlvoContextAccessor` is an `AsyncLocal` holder (`src/MMLib.Alvo/Auth/Internal/AlvoContextAccessor.cs`); a value an async method assigns is not visible after the method yields, so from the second `MoveNextAsync` on — i.e. once the assistant has streamed its first update — the inner stream (and the tools the agent framework invokes inside it) runs with no principal, and `IAlvoManagement` answers forbidden. The e2e suite never saw it: `ScriptedAssistant` replaces `AlvoAssistant` and yields `ToolInvoked` without calling a tool.

**Fix:** publish the caller before **every** step of the inner enumerator — resolve the caller once, then drive `GetAsyncEnumerator(ct)` by hand, setting `ambient.Principal = caller` immediately before each `MoveNextAsync()` and restoring the previous value after it (and in `finally`), so no principal leaks into the consumer between steps. Check the non-streaming `AsOperatorAsync<T>(Func<Task<T>>, …)` overload for the same flaw.

**Tests (must fail before the fix):**
- `test/MMLib.Alvo.Admin.Tests/Internal/` — a unit test over `ManagementGateway.AsOperatorAsync` with a fake stream that yields one item and then, on its **second** step, reads `IAlvoContextAccessor.Principal` and yields it: the second item must be the operator, not null. (Use the real `AlvoContextAccessor` semantics — if it is internal to core and not visible, use an `AsyncLocal`-backed test double with the same box semantics and say so.)
- An integration-level guard that runs the real `AlvoAssistant` with a fake `IChatClient` (Microsoft.Extensions.AI test double) that first streams text, then requests the `get_schema` tool, and asserts the tool result is the schema and not a forbidden refusal — in whichever test project already references `MMLib.Alvo.Ai` and the in-process management (find it; if none can host it cheaply, say so and keep the unit test).

This touches the auth context the core authorizes on — run the `alvo-security-core-review` checklist items that apply (default-deny must still hold: outside the operator's steps the ambient principal is the previous value, never the operator) and state them in the report.

**Commit:** `fix(f5): the assistant's tools run as the operator on every step of the turn`.

### Task A: The assistant is a side panel opened from the header

**Files:** `Components/Assistant/AssistantDrawer.razor`, `Components/Shell/AdminLayout.razor`, `wwwroot/alvo.css` (the `.a-assistant*` block), `docs/design/gallery.html` (show the panel), `test/MMLib.Alvo.Admin.Tests.EndToEnd/AssistantScenarios.cs`.

- **Launcher:** a normal header button beside Search/theme — `a-btn a-btn--ghost a-btn--sm` with a sparkle/chat icon from `Icons` (add one path if none fits) and the text *Ask Alvo*, `aria-expanded`, `data-testid="assistant-launch"`, rendered only when an assistant is mounted (today's rule). No floating pill. On a phone the header has room for an icon-only button with `aria-label="Ask Alvo"`.
- **Panel:** a right-hand side panel, full height of the shell below the header (or of the viewport — choose, and say why), fixed width `min(440px, 100vw)` on desktop, full width on a phone; `role="complementary"` with `aria-label="Ask Alvo"` (it is not modal: the operator keeps working in the page beside it — so no scrim and no scroll lock, and Escape closes it); header row = title *Ask Alvo* + subtitle + a close icon button (`aria-label="Close the assistant"`); the thread scrolls; the ask box is pinned at the bottom. Use surface/border/shadow tokens the way `.a-sheet` does so it reads as the same family. It must not cover the pending bar or the header.
- **Ask button:** the design system's primary button (`a-btn a-btn--primary`), whatever makes its label AA in both themes — if `a-btn--primary` itself fails AA in either theme, fix the token pair (it is used elsewhere; say so) rather than special-casing the assistant.
- **Focus:** opening moves focus to the question box; closing returns it to the launcher (`FocusReturn` / `FocusOnRender` exist).
- **E2E:** update the scenarios for the new layout; add one asserting the panel is full height (its bottom within 1 px of the viewport bottom at 1400×950) and that it does not overlap the header; one at 375 px that it is full width; `AssertConsoleClean`.
- **Commit:** `fix(f5): the assistant is a side panel opened from the header`.

### Task B: A failed turn says what failed

**Files:** `src/MMLib.Alvo.Ai/AlvoAssistant.cs` (+ its tests under `test/MMLib.Alvo.Ai.Tests*` — find them), `src/MMLib.Alvo/Ai/Internal/AiConnectionResolver.cs` (+ tests).

- In `AlvoAssistant`'s endpoint-failure catch, classify the exception: `System.ClientModel.ClientResultException` with `Status` 401/403 → *"The AI provider refused the key. Check the key the connection uses — under Settings, or `Alvo:Ai:ApiKeySecretRef` and the secret it names — and this instance's log for the provider's answer."*; 404 → *"The AI provider has no model called '{model}' for this key."* (the model name is configuration and already shown on Settings, so it may appear); 429 → rate-limited/quota; other status → *"The AI provider answered with an error ({status})."*; `HttpRequestException` / timeout / DNS → today's *"did not answer"* sentence (now true). Keep one `TurnFailed` log with the exception. Unit-test the classifier (pure static over an exception → sentence).
- In `AiConnectionResolver`, when `Alvo:Ai:ApiKeySecretRef` names a secret the store does not have, log a warning once per resolve by **name** (never value) — *"Alvo:Ai:ApiKeySecretRef names secret '{name}', which this instance does not have; the connection is used without a key."* — and when `ApiKeySecretRef` is absent and the endpoint host is `api.openai.com` or ends in `.openai.azure.com`, log that such an endpoint always needs a key. Test both with the existing resolver test fixture.
- File a follow-up line in `docs/todo-admin.md` §8d (next free number): Settings says *connected* for a connection whose key is missing; the answer needs `ManagementInfo.Ai` to carry a key state (Abstractions change).
- **Commit:** `fix(f5): a failed assistant turn says whether the key, the model or the address failed`.

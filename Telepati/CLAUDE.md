# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build                                             # whole solution
dotnet test                                              # all 58 tests
dotnet test --filter "FullyQualifiedName~MessagingTests" # one class
dotnet test --filter "Nearby_search"                     # one test

dotnet run --project src/Telepati.Server    # https://localhost:7180 (+ gRPC on 7181), Swagger at /swagger
dotnet run --project src/Telepati.Web       # https://localhost:7200  messenger
dotnet run --project src/Telepati.Admin     # https://localhost:7210  admin console
dotnet run --project src/Telepati.Desktop   # Avalonia window

dotnet build src/Telepati.Mobile -f net10.0-android      # MAUI; iOS/MacCatalyst need a macOS host

dotnet run --project tools/Telepati.Bench -c Release     # perf benchmark -> Performance.md
bash tools/bench-http.sh                                 # HTTP timings (server must be running)
dotnet run --project tools/Telepati.Shots                # screenshots (all 3 apps must be running)

# Bot against a real model — credentials come from the environment, never a tracked file
TELEPATI_BOT_PROVIDER=AzureOpenAI TELEPATI_BOT_MODEL=gpt-5-mini TELEPATI_BOT_APIKEY=... TELEPATI_BOT_ENDPOINT=https://<resource>.openai.azure.com/ TELEPATI_TAVILY_KEY=... dotnet run --project tools/Telepati.BotTest
```

The server seeds SQLite on first run. Demo accounts all use `Telepati123!`; `kangfadhil` is Super Admin. Delete `src/Telepati.Server/telepati.db*` to reseed.

Kestrel binds ports from `appsettings.json`, so `--urls` is ignored.

## Architecture

Requirements are in `requirements.md` (Indonesian). Roadmap in `Plan.md`, current state in `Progress.md`, deep dives in `docs/`.

```
Domain → Shared → Infrastructure → { Server, Bot }
                                 ↘ Client.Core → UI → { Web, Desktop, Mobile }
Admin references Infrastructure directly (it needs DB reach the client transports don't expose).
```

### The four constraints that shape everything

1. **Three transports, one set of logic.** `TelepatiHub` (SignalR), `TelepatiGrpcService`, and the Minimal API endpoints all delegate to the same services in `Telepati.Infrastructure` and contain no business logic. Domain services push through `IRealtimeNotifier`; the server registers `CompositeRealtimeNotifier`, which fans out to SignalR groups *and* gRPC streams because one user may hold both at once. Clients see only `ITelepatiClient`; `TransportSwitcher` swaps implementations at runtime.

2. **Every infrastructure dependency is provider-swappable from config.** Database (SQLite/SqlServer/MySql/PostgreSql, with `ShardResolver` hashing on `ChatId`), cache (Memory/Redis), storage (FileSystem/AzureBlob/S3/MinIO), AI model (OpenAI/Anthropic/Gemini/Ollama). All selected in `DependencyInjection.AddTelepatiInfrastructure`.

3. **Config layers: appsettings → `AppSettings` table → effective value.** `SettingsService.GetOptionsAsync()` applies DB overrides on top of the bound `TelepatiOptions`. The admin settings catalogue is derived by reflection over the options tree, so a new property appears in the UI with no extra code.

4. **One UI, four hosts.** Routable pages live in `Telepati.UI/Pages`, not in the web app. Each host points its `Router` at that assembly via `AdditionalAssemblies` **and** calls `.AddAdditionalAssemblies(...)` on `MapRazorComponents` — the Router only covers client-side navigation; without the second call every URL 404s. Desktop runs Blazor Server in-process on loopback and points Avalonia's `NativeWebView` at it — Avalonia ships no BlazorWebView control.

5. **Clients cache what they have already fetched.** `Telepati.Client.Data` decorates the active transport with `CachedTelepatiClient`, backed by SQLite (default) or LiteDB — so all three transports get the cache without knowing. The web app uses IndexedDB instead (`telepati-store.js`); `localStorage` was rejected because it is synchronous, ~5 MB, string-only and unindexed. See `docs/client-storage.md`.

### Data model notes that are easy to get wrong

- One `Chats` table holds direct, group, channel, bot and saved-messages conversations, distinguished by `ChatType`.
- Direct chats store no title — `ChatService.ResolveTitleAsync` resolves it per viewer.
- Every message writes one `MessageReceipt` per recipient; that is what makes delivery reports possible.
- **`UnreadCount` is only ever written via `ExecuteUpdateAsync`, on both the increment and the clear.** Mixing set-based SQL with a tracked entity lets a stale instance overwrite a counter the same context just bumped.
- **Do not set `QueryTrackingBehavior.NoTracking` globally.** It silently turns every "load, mutate, SaveChanges" write in the service layer into a no-op. Read paths opt out per query with `AsNoTracking()`.
- Encrypted chats store ciphertext only, so search deliberately skips them.
- **Multi-include reads need `AsSplitQuery()`.** Attachments, Reactions and Mentions are three collections on one root; a single query multiplies them together. Measured 85.8% faster on a busy conversation (`Performance.md`).

### Bot

Providers: OpenAI, **AzureOpenAI** (`Model` is the deployment name), **DeepSeek**, Anthropic, Gemini, Ollama. The OpenAI connector also takes a custom `Endpoint`, which covers any OpenAI-compatible service. Verified end to end against Azure `gpt-5-mini` and DeepSeek `deepseek-v4-flash` — 16/16 checks, see `docs/bot.md`.

`BotService` keys sessions on `(ChatId, UserId, BotHandle)` — group sessions carry `ChatId`, direct sessions carry `UserId`. In groups it only answers when mentioned. `#resetbot` clears history *and* restores the configured persona; `#newpersona [text]` overrides it for that session. Auto-compaction summarises old turns past `AutoCompactThreshold`.

Bot output is never trusted: `MarkdownRenderer` disables raw HTML then sanitises again, and every file path goes through `Workspace.Resolve`, which rejects absolute paths and traversal.

`ChatOrchestrator` runs the bot turn in a background task with its own DI scope, so the sender never waits on the model.

### Skills and MCP

Both are managed from the admin console and stored in the database (`SkillRepositories`,
`SkillDefinitions`, `McpServerDefinitions`) — not appsettings, because they are edited while the
app runs. See `docs/skills-and-mcp.md`.

A skill is a folder with `SKILL.md` plus optional scripts and references. `SkillsPlugin` exposes
it through progressive disclosure — the model sees only name + description until it picks one —
so twenty installed skills do not mean twenty full instruction blocks per turn.

**Script execution has three independent gates, and all three must pass**: the global
`Bot:EnableCodeExecution`, the per-skill `AllowScriptExecution` (off at install — installing and
authorising are deliberately separate decisions), and the `Bot:AllowedExecutors` allow-list.
That list defaults to `powershell, python, dotnet, cmd, node`, so `.py`, `.ps1` and `.js` skills
run out of the box; `bash` is deliberately left out, so `.sh` is refused until an admin adds it.
Adding a name to the allow-list is not enough on its own — `ScriptPlugin.BuildCommandAsync` and
`ScriptFileFor` must both know the executor, or the gate passes and the run fails as unrecognised.

MCP servers seed **disabled**, 28 of them. `McpToolProvider` caches clients per process (a stdio
server is a child process) and puts failures in a 5-minute cooldown so one broken server cannot
slow every reply.

## Traps this codebase has already hit

Each of these shipped as a real bug and was fixed; the pattern is easy to reintroduce.

- **`app.UseStaticFiles()` is not enough.** RCL `_content/*` and `_framework/*` assets only load automatically in Development. Use `MapStaticAssets()` plus `builder.WebHost.UseStaticWebAssets()`, or Blazor never becomes interactive outside Development and forms silently post as plain HTML.
- **A string parameter without `@` is a literal.** `TransportName="State.TransportName"` renders that text verbatim.
- **A Razor comment inside a component's attribute list is parsed as an attribute name** and throws at render time. Put it above the tag.
- **No JS interop in `OnInitializedAsync`.** Navigation tears the circuit down mid-await and it surfaces as `ObjectDisposedException` with a frozen page. Use `OnAfterRenderAsync` or a user action.
- **Theme variables set on an inner element do not change inherited `color`.** `body { color: var(--tp-text) }` resolves at `:root`; the wrapper needs `.tp-root` to re-declare colour and background, or dark mode renders unreadable text.
- **Never hold a signed-in identity in a scoped Blazor service.** The admin console did, and every full-page navigation and every F5 logged the admin out. Identity belongs in an auth cookie; `AdminSession` reads it from claims and re-checks the role against the database.
- **Reasoning models reject `max_tokens`, `temperature` and `top_p`** with HTTP 400. `KernelFactory.IsReasoningModel` detects the `o1`/`o3`/`o4`/`gpt-5` families and sends none of them.
- **A model can return empty content**, usually after a tool round trip. `BotService` retries once then answers with a clear message; the failed turn is deliberately not persisted.
- **Storage URLs are server-relative, and only the server can resolve them.** The FileSystem provider returns `/files/…`, which a browser resolves against whichever host rendered the page — the messenger on :7200, not the server on :7180 — and desktop/mobile have no web origin at all. Every image, video, avatar and attachment 404s. Render media through `MediaResolver`, which prefixes `ClientOptions.ServerUrl` and passes absolute cloud URLs through untouched.
- **A masked secret sent back from a form will overwrite the real one.** The MCP editor shows `••••••` for stored values; saving posts the mask back. `MergeEnvironment` restores the stored value for any key that returns still masked, so editing an unrelated field cannot destroy a working API key.
- **The messenger's right pane is shared.** Adding a left-hand list (status, and anything like it) is not enough — without a branch in the right pane it keeps rendering the last conversation, which is how opening a status showed a chat thread instead.

## Conventions

- User-facing strings, error messages, and code comments in Razor/services are Indonesian; type and member names are English.
- Minimal API query parameters for paging are `int?` with defaults via `QueryDefaults` — a required `int` returns 400 when the caller omits it.
- The design system is `src/Telepati.UI/wwwroot/css/telepati.css`; six CSS custom properties drive every colour, and themes only set those six.
- Performance claims belong in `Performance.md` with the measurement that backs them. The benchmark harness exists precisely so a claim can be checked rather than asserted.

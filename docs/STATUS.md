# Status

**Updated:** 2026-08-12 · **Milestone:** M0 complete, eight of eight exit criteria met

This file records **state**. [IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md)
records **intent** — what each milestone is for and how it ends. Read the plan
to know where the project is going; read this to know where it actually is.

Keep it current at the end of a working session, not during. A status file
updated speculatively is worse than none.

---

## At a glance

| | |
|---|---|
| Milestone | M0 (walking skeleton), **8 of 8** exit criteria — complete |
| Commits | 28 |
| Architecture decisions | 12 |
| Tests | 43, all shown capable of failing |
| Registered tools | 1 of 7 (`create_shopping_item`) |
| Mapped tables | 3 of 28 (`memories`, `shopping_items`, `ai_actions`) |
| Eval cases running | 4 of 55 |

The system works end to end today: text typed into a page is extracted by a
local model, validated and executed through the tool layer, written under
row-level security, audited, and reported back — in under two seconds against a
warm model, from a container built by `docker compose up`.

**M0 is closed. The next branch is M1, identity.**

---

## M0 · Walking skeleton

| Exit criterion | State |
|---|---|
| `docker compose up` reaches a healthy stack, three containers | done — verified from an empty volume; healthy `api` means `/health` reached Postgres |
| EF model agrees with `data/schema.sql`; parity test passes | done — 3 assertions (ADR 0012: SQL owns the schema, no migrations) |
| App connects as a role that is neither superuser nor `BYPASSRLS` | done — and the test suite refuses to run if it is |
| Partner B cannot read Partner A's private memory through `DbContext` | done |
| A request without session context returns zero rows | done — asserted, not incidental |
| Pooling test, interleaved requests, zero cross-contamination | done — 50 in C#, 1600 in SQL |
| One capture creates one shopping item and one `ai_actions` row | done — verified in a browser |
| Eval harness exists and runs, 3 cases wired in | done — 4 run; `EvalCoverage` reports the 28 blocked |

**Remaining:** nothing. The assumption M0 existed to falsify — that row-level
security might not survive EF Core's connection pooling — did not falsify, and
the walking skeleton walks.

---

## What exists

### Database
- `data/schema.sql` is authoritative (ADR 0012). 28 tables, 16 with `FORCE ROW LEVEL SECURITY`.
- `data/rls-tests.sql` — 33 assertions: read isolation, write path, connection reuse, prepared statements under `force_generic_plan`, privilege escalation.
- `data/rls-concurrency.sh` — 1600 interleaved transactions across 16 reused connections, `-M prepared`.
- Both verified to fail when a policy is removed.

### Application
- `IScopedUnitOfWork` / `ICoupleTransaction` — every read and write passes through a transaction scoped with `set_config(..., true)`.
- `ICoupleScopeAccessor` / `ICoupleScopeSetter` — reading and establishing the security context are separately grantable.
- Tool pipeline — `ITool`, `IToolRegistry`, `IToolDispatcher`, `AuditingToolDispatcher`. Forbidden arguments and undeclared properties are refused by the dispatcher, not by each tool.
- `ICaptureProcessor` — joins model to tool layer. Orchestrates only.
- `CapturePrompt` — versioned (`2026-08-12.1`), because the eval set judges this exact text.

### AI
- `ILlmProvider` in Application; `OllamaLlmProvider` in `CoupleOS.AI`. Application never references the AI project.
- Model: `qwen3.5:4b`, thinking disabled, `num_ctx` explicit. Selection and measurements in [ADR 0011](../decisions/0011-local-model-provider-for-development.md).

### Web
- Razor Pages + htmx, no Bootstrap or jQuery (ADR 0010). htmx vendored locally, not from a CDN.
- One page: `shared.md` editor, Process button, change report.
- `DevelopmentScopeMiddleware` stands in for authentication and throws outside Development.
- `/health` — plain text, unauthenticated, backed by `DatabaseHealthCheck`. Runs `SELECT 1`, so a healthy answer means the app reached Postgres as the non-superuser role. `CanConnectAsync` was the obvious implementation and the wrong one: it swallows the provider exception and returns a bare false, discarding the only part worth reading.

### Container
- `src/CoupleOS.Api/Dockerfile` — SDK build stage, `aspnet:10.0` runtime, non-root (uid 1654), 368 MB. Restore is a separate layer from the sources.
- `.dockerignore` keeps host `bin/`, `obj/`, `.env` and `appsettings.Development.json` out of the context, so the image built locally is the image built from a clean clone, and no secret reaches a layer.
- `api` is in the default compose stack. The `app` profile was waiting for the project to exist; it does.
- The runtime image ships no HTTP client at all — no curl, no wget, no netcat — so `curl` is installed for `HEALTHCHECK`. Without it `docker compose ps` could only ever say *running*, never *healthy*.
- The probe matches the response body rather than trusting curl's exit code, because `--fail` treats a 3xx as success and `UseHttpsRedirection` answers 307 if an HTTPS port is ever configured.

---

## What is not built

| | Milestone |
|---|---|
| Authentication — magic links, sessions, rate limiting, couple invitations | M1 |
| Block segmentation, content hashing, re-`Process` doing nothing | M2 |
| The private chat surface | M2 |
| `dump_files` / `dump_blocks` / `dump_runs` persistence | M2 |
| Change report persisted to `dump_runs.report` | M2 |
| `needs_input` parking and `request_clarification` implementation | M2 |
| Six of seven tools — task, reminder, expense, event, memory, search | M3 |
| Eval gates enforced as a build gate | M4 |
| Attachments and the read surface | M5 |

---

## Debts and deferred decisions

Each of these is deliberate and recorded where the code lives. Listed here so
they are visible in one place rather than discoverable only by reading commits.

**Blocking a specific future step**

1. **`action_outcome` has no `confirmation_required` label.** `AiActionAuditSink` throws rather than substituting a near-enough value. A migration is owed before the first `Confirm`-tier tool ships. *(ADR 0008, M3)*
2. **`memories` is mapped read-only.** `type` and `content` are `NOT NULL` with no default and are unmapped, so `create_memory` cannot be written until the entity carries them. `SchemaParityTests` records this rather than tolerating it silently. *(M3)*
3. **`/health` is unauthenticated, and must stay that way.** The container probe has no credentials. When M1 adds sessions the endpoint needs an explicit allow, and it must not start reporting anything a stranger should not see — status only, never the exception text the logs carry. *(M1)*
4. **Data protection keys are not persisted.** The container writes them to `/home/app/.aspnet/DataProtection-Keys`, which dies with the container, so antiforgery tokens do not survive a rebuild — a page left open across `docker compose up --build` fails its next `Process` with a 400. Harmless now; at M1 it silently logs everyone out on every deploy. *(M1)*

**Design questions with a real answer needed later**

5. **The change report describes actions, not input.** A line producing no tool call vanishes without trace — observed on the first real run, where an event silently disappeared. M2's exit criteria now require the report to account for every block. *(M2)*
6. **One transaction per capture run.** Correct for a five-line note, questionable for a two-hundred-line dump where a late failure discards earlier successes. *(M2)*
7. **Prompt cost scales with the tool catalogue.** Measured 660 prompt tokens for 3 tools; 17 tools projects to ~3060 per block, and ~62s for a 20-block dump. Two levers recorded in ADR 0011: filter tools per block, or batch blocks per call. *(M3)*
8. **`IScopedUnitOfWork` is a seam by convention, not construction.** Nothing stops a future caller injecting `CoupleOsDbContext` directly. Row-level security makes that fail closed rather than leak, so the consequence is an empty list rather than a breach — but the type system does not enforce it. `DevelopmentSeeder` and `DatabaseHealthCheck` are the two deliberate exceptions, each documenting why it reads no rows.

**Smaller**

9. **`CaptureProcessor` has no unit tests.** Its behaviour is only covered by the manual browser run and indirectly by the pipeline tests. It was built to be testable against a fake provider; that test has not been written.
10. **Three AI provider tests each make a separate model call** for what is one call's worth of assertions — about 6s of GPU per run, and three chances for a non-deterministic model to disagree with itself.
11. **`LlmUsage.Duration` includes HTTP and deserialisation**, so it reads slightly high. Fine for cost auditing, misleading as a benchmark.
12. **`Temperature = 0` is hard-coded** in `OllamaLlmProvider` rather than an option. Right for extraction; the wrong place for the decision to live if the chat surface ever wants warmth.
13. **Base images float on the `10.0` tag, not a digest.** The build is reproducible in the sense that matters today — restore is pinned by `Directory.Packages.props` — but two builds a month apart can sit on different SDK patches. Pin when there is somewhere to deploy to. *(M5)*
14. **No CI.** Deliberately deferred. "CI gate" currently means a command someone remembers to run.

---

## Things proven, and worth not re-litigating

- Row-level security holds under connection pooling **and** prepared statements, including `force_generic_plan`. *(ADR 0005 verification section)*
- `SET LOCAL` cannot take a query parameter; `set_config(name, value, true)` is exactly equivalent and parameterises. Session-level `SET` leaks across pooled transactions — reproduced in three statements.
- A superuser connection bypasses every policy. The app role is `NOSUPERUSER NOBYPASSRLS`, and the test suite refuses to run as anything else.
- `qwen3.5:4b` fits entirely in 8 GB of VRAM and answers a block in ~1.9s warm; `qwen3.5:9b` spills 12% to CPU for identical output.
- Disabling thinking removed 82% of generated tokens and made the same call 4.6x faster.
- `pg_isready` over the unix socket reports ready **while the init scripts are still running** — the entrypoint's temporary server has `listen_addresses=''`, so it is unreachable over TCP but not over the socket. The db healthcheck requires `-h 127.0.0.1` for that reason; without it `api` can start against a database whose 28 tables do not exist yet.
- `mcr.microsoft.com/dotnet/aspnet:10.0` contains no HTTP client. Verified: no curl, no wget, no netcat. Only `openssl`, which cannot speak plain HTTP.
- `DbContext.Database.CanConnectAsync()` returns false rather than throwing, discarding the reason. Anything that needs to report *why* the database is unreachable has to issue the statement itself.

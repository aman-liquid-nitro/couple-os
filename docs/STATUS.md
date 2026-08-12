# Status

**Updated:** 2026-08-12 · **Milestone:** M0, seven of eight exit criteria met

This file records **state**. [IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md)
records **intent** — what each milestone is for and how it ends. Read the plan
to know where the project is going; read this to know where it actually is.

Keep it current at the end of a working session, not during. A status file
updated speculatively is worse than none.

---

## At a glance

| | |
|---|---|
| Milestone | M0 (walking skeleton), 7 of 8 exit criteria |
| Commits | 26 |
| Architecture decisions | 12 |
| Tests | 35, all shown capable of failing |
| Registered tools | 1 of 7 (`create_shopping_item`) |
| Mapped tables | 3 of 28 (`memories`, `shopping_items`, `ai_actions`) |
| Eval cases running | 4 of 55 |

The system works end to end today: text typed into a page is extracted by a
local model, validated and executed through the tool layer, written under
row-level security, audited, and reported back — in about seven seconds.

---

## M0 · Walking skeleton

| Exit criterion | State |
|---|---|
| `docker compose up` reaches a healthy stack, three containers | **partial** — `db` and `maildev` healthy; `api` has no Dockerfile |
| EF model agrees with `data/schema.sql`; parity test passes | done — 3 assertions (ADR 0012: SQL owns the schema, no migrations) |
| App connects as a role that is neither superuser nor `BYPASSRLS` | done — and the test suite refuses to run if it is |
| Partner B cannot read Partner A's private memory through `DbContext` | done |
| A request without session context returns zero rows | done — asserted, not incidental |
| Pooling test, interleaved requests, zero cross-contamination | done — 50 in C#, 1600 in SQL |
| One capture creates one shopping item and one `ai_actions` row | done — verified in a browser |
| Eval harness exists and runs, 3 cases wired in | done — 4 run; `EvalCoverage` reports the 28 blocked |

**Remaining:** a Dockerfile for `src/CoupleOS.Api`, so `docker compose --profile app up -d`
runs the whole stack. Roughly half an hour.

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
3. **No `api` Dockerfile.** The compose `app` profile cannot build. *(M0)*

**Design questions with a real answer needed later**

4. **The change report describes actions, not input.** A line producing no tool call vanishes without trace — observed on the first real run, where an event silently disappeared. M2's exit criteria now require the report to account for every block. *(M2)*
5. **One transaction per capture run.** Correct for a five-line note, questionable for a two-hundred-line dump where a late failure discards earlier successes. *(M2)*
6. **Prompt cost scales with the tool catalogue.** Measured 660 prompt tokens for 3 tools; 17 tools projects to ~3060 per block, and ~62s for a 20-block dump. Two levers recorded in ADR 0011: filter tools per block, or batch blocks per call. *(M3)*
7. **`IScopedUnitOfWork` is a seam by convention, not construction.** Nothing stops a future caller injecting `CoupleOsDbContext` directly. Row-level security makes that fail closed rather than leak, so the consequence is an empty list rather than a breach — but the type system does not enforce it.

**Smaller**

8. **`CaptureProcessor` has no unit tests.** Its behaviour is only covered by the manual browser run and indirectly by the pipeline tests. It was built to be testable against a fake provider; that test has not been written.
9. **Three AI provider tests each make a separate model call** for what is one call's worth of assertions — about 6s of GPU per run, and three chances for a non-deterministic model to disagree with itself.
10. **`LlmUsage.Duration` includes HTTP and deserialisation**, so it reads slightly high. Fine for cost auditing, misleading as a benchmark.
11. **`Temperature = 0` is hard-coded** in `OllamaLlmProvider` rather than an option. Right for extraction; the wrong place for the decision to live if the chat surface ever wants warmth.
12. **No CI.** Deliberately deferred. "CI gate" currently means a command someone remembers to run.

---

## Things proven, and worth not re-litigating

- Row-level security holds under connection pooling **and** prepared statements, including `force_generic_plan`. *(ADR 0005 verification section)*
- `SET LOCAL` cannot take a query parameter; `set_config(name, value, true)` is exactly equivalent and parameterises. Session-level `SET` leaks across pooled transactions — reproduced in three statements.
- A superuser connection bypasses every policy. The app role is `NOSUPERUSER NOBYPASSRLS`, and the test suite refuses to run as anything else.
- `qwen3.5:4b` fits entirely in 8 GB of VRAM and answers a block in ~1.9s warm; `qwen3.5:9b` spills 12% to CPU for identical output.
- Disabling thinking removed 82% of generated tokens and made the same call 4.6x faster.

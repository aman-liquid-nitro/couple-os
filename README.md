# Couple OS

A private, intelligent memory and coordination layer for a couple's everyday life.

**Status:** M0 and M1 complete. Two people sign in with no password anywhere in
the system, form a couple, and each write to it as themselves — a note typed
into a page becomes rows, under row-level security, with an audit trail and a
change report.
**Next target:** V0 (see [docs/V0_SCOPE.md](./docs/V0_SCOPE.md)) — not the roadmap.
**Next step:** M2, capture surfaces — see [docs/IMPLEMENTATION_PLAN.md](./docs/IMPLEMENTATION_PLAN.md).

## Start here

| Read | For |
|---|---|
| [docs/V0_SCOPE.md](./docs/V0_SCOPE.md) | What is actually being built first, and what is cut |
| [docs/IMPLEMENTATION_PLAN.md](./docs/IMPLEMENTATION_PLAN.md) | How V0 gets built — milestones, exit criteria, risks |
| [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) | Shape of the system, domain model, request pipeline |
| [docs/TOOLS.md](./docs/TOOLS.md) | Contracts for the seven V0 tools |
| [decisions/](./decisions) | Why things are the way they are (ADRs 0001–0013) |
| [docs/SPEC.md](./docs/SPEC.md) | The original full specification |

## Where things live

| Folder | Contents |
|---|---|
| `docs/` | Specification, architecture, scope, tool contracts |
| `decisions/` | Architecture Decision Records |
| `data/` | Reference schema, AI evaluation dataset |
| `product/` | User stories, flows, module briefs, UI copy |
| `design/` | Wireframes, dashboard mockups, visual design |
| `research/` | Model/provider comparisons, cost analysis, prior art |
| `assets/` | Images, exports, screenshots |

## How capture works

Two surfaces. **Where you write determines who can see it** — the AI never guesses.

```text
shared.md            both partners write · append-only log · batch Process
                     → everything here is shared

private chat         one thread each · conversational · surprises, gifts, plans
                     → everything here is private
```

Processing a dump returns a **change report**: what was created, updated,
superseded, and what failed. Corrections are new lines, never edits.

See [ADR 0009](./decisions/0009-capture-surfaces-split-by-scope.md).

## Core principle

Capture → Understand → Remember → Organize → Act → Learn

## Non-negotiables

1. The LLM is never the source of truth for structured data (SPEC.md §56.6).
2. All calculations are deterministic application code, never LLM arithmetic (§56.7).
3. Every record carries a visibility scope, inherited from its capture surface and enforced by the database (ADR 0005, 0009).
4. Never claim an action succeeded when the tool failed (§46).
5. Prove the core interaction is useful before building outward (§56.1).

## Decisions taken

| ADR | Decision |
|---|---|
| [0001](./decisions/0001-modular-monolith.md) | Modular monolith, one deployable, one database |
| [0002](./decisions/0002-postgresql-pgvector.md) | PostgreSQL + pgvector; no dedicated vector database |
| [0003](./decisions/0003-llm-provider-abstraction-and-model-routing.md) | Provider abstraction with role-based routing (`fast`/`deep`/`embed`/`local`) |
| [0004](./decisions/0004-tool-layer-as-sole-llm-write-path.md) | The tool layer is the only path from LLM to state |
| [0005](./decisions/0005-visibility-scope-enforcement.md) | Three-value visibility, enforced by row-level security |
| [0006](./decisions/0006-memory-taxonomy-and-confidence.md) | Memory taxonomy, confidence, and the inference boundary |
| [0007](./decisions/0007-authentication-magic-links.md) | Magic-link authentication, no passwords |
| [0008](./decisions/0008-notification-usefulness-threshold.md) | Notification scoring threshold and confirmation tiers |
| [0009](./decisions/0009-capture-surfaces-split-by-scope.md) | Two capture surfaces, split by visibility scope |
| [0010](./decisions/0010-server-rendered-ui-for-v0.md) | Server-rendered UI with htmx for V0 |
| [0011](./decisions/0011-local-model-provider-for-development.md) | Ollama for development, Anthropic for validation |
| [0012](./decisions/0012-sql-owns-the-schema.md) | The database schema is owned by SQL, not EF migrations |
| [0013](./decisions/0013-ollama-hosted-service-for-inference.md) | Ollama's hosted service for inference; local stays the fallback |

Two contradictions in the original specification are resolved by these:
`PRIVATE`/`SHARED` versus the three-scope model (§9 vs §28) in ADR 0005, and
`search_memory` depending on a later milestone (§36 vs §54) in `docs/V0_SCOPE.md`.
ADR 0009 supersedes §7's single chat inbox, and ADR 0010 supersedes §41's
Next.js frontend for V0.

## Where the project is

[docs/STATUS.md](./docs/STATUS.md) — what exists, what does not, and every
recorded debt in one place. Start there.

## Running it

Requires Docker, and nothing else — the API is built inside the image, so no
local .NET SDK is needed to run the stack. One command, three containers:

```bash
cp .env.example .env && docker compose up -d
```

Then <http://localhost:8080>. Wait for all three to report `healthy`:

```bash
docker compose ps
```

`api` reports healthy only once `/health` has reached Postgres as the
non-superuser role, so a healthy stack means the application can actually read
and write — not merely that three processes started. If it sits on `starting`,
`docker compose logs api` says why in its first few lines.

The database builds itself from `data/schema.sql` on first start and creates
the non-superuser application role. Verify row-level security actually holds:

```bash
docker compose exec -u postgres db psql -d coupleos -v ON_ERROR_STOP=1 -f /repo/data/rls-tests.sql
```

```bash
docker compose exec -u postgres -e PGDATABASE=coupleos db bash /repo/data/rls-concurrency.sh
```

Expect 33 assertions passing, then `PASS — 1600 interleaved transactions,
0 cross-couple leaks`. Both exit non-zero on failure, and both are verified to
fail when a policy is removed, so a green run means something.

The application suite needs the .NET SDK and the running stack. It shares its
fixture identifiers with `rls-tests.sql`, and both re-seed rather than assume an
empty database, so they can run in either order:

```bash
dotnet test
```

Expect 96 passing. It refuses to run at all if pointed at a superuser or
`BYPASSRLS` role, because every isolation assertion would then be meaningless.

Thirteen of those call a real model, because ADR 0004 makes tool calls the only
write path and mocking one would test the mock. They read the model host from the
environment, and `dotnet test` does not load `.env` itself — so export it first:

```bash
set -a; . ./.env; set +a; dotnet test
```

Without that they fall back to a local Ollama on `localhost:11434` and tell you so
if none is running.

Schema changed? The init scripts only run on an empty volume:

```bash
docker compose down -v && docker compose up -d
```

## Signing in

There is no seeded account, and no password to set. Open
<http://localhost:8080>, enter any address, and collect the link from maildev at
<http://localhost:1080> — nothing is sent anywhere real in development, and the
link is also written to `docker compose logs api` if maildev is not running.

The first link creates your account; then create a couple and invite your
partner with a second address. Links work once and expire after 15 minutes, and
you get three per address per fifteen minutes.

Unknown addresses are answered exactly as known ones are, so the sign-in screen
cannot be used to find out who has an account (ADR 0007).

## Stack

```text
ASP.NET Core · Razor Pages + htmx · PostgreSQL 16 + pgvector · EF Core
Anthropic (fast + deep roles) behind ILLMProvider · docker compose

The application connects as a non-superuser role. A superuser connection
bypasses every row-level security policy and silently voids ADR 0005.
```

---

Private project. Personal use only.

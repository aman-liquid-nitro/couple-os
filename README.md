# Couple OS

A private, intelligent memory and coordination layer for a couple's everyday life.

**Status:** Specification and architecture complete. Implementation not started.
**Next target:** V0 (see [docs/V0_SCOPE.md](./docs/V0_SCOPE.md)) — not the roadmap.
**Next step:** M0, the walking skeleton — see [docs/IMPLEMENTATION_PLAN.md](./docs/IMPLEMENTATION_PLAN.md).

## Start here

| Read | For |
|---|---|
| [docs/V0_SCOPE.md](./docs/V0_SCOPE.md) | What is actually being built first, and what is cut |
| [docs/IMPLEMENTATION_PLAN.md](./docs/IMPLEMENTATION_PLAN.md) | How V0 gets built — milestones, exit criteria, risks |
| [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md) | Shape of the system, domain model, request pipeline |
| [docs/TOOLS.md](./docs/TOOLS.md) | Contracts for the seven V0 tools |
| [decisions/](./decisions) | Why things are the way they are (ADRs 0001–0008) |
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

Two contradictions in the original specification are resolved by these:
`PRIVATE`/`SHARED` versus the three-scope model (§9 vs §28) in ADR 0005, and
`search_memory` depending on a later milestone (§36 vs §54) in `docs/V0_SCOPE.md`.
ADR 0009 supersedes §7's single chat inbox, and ADR 0010 supersedes §41's
Next.js frontend for V0.

## Running it

Requires Docker. No .NET is needed until Milestone M0 creates the projects.

```bash
cp .env.example .env          # .env is gitignored; never commit it
docker compose up -d          # postgres + maildev
```

The database builds itself from `data/schema.sql` on first start and creates
the non-superuser application role. Verify row-level security actually holds:

```bash
docker compose exec -u postgres db \
  psql -d coupleos -v ON_ERROR_STOP=1 -f /repo/data/rls-tests.sql

docker compose exec -u postgres -e PGDATABASE=coupleos db \
  bash /repo/data/rls-concurrency.sh
```

Expect 33 assertions passing, then `PASS — 1600 interleaved transactions,
0 cross-couple leaks`. Both exit non-zero on failure, and both are verified to
fail when a policy is removed, so a green run means something.

Schema changed? The init scripts only run on an empty volume:

```bash
docker compose down -v && docker compose up -d
```

Magic-link emails are caught by maildev at http://localhost:1080 — nothing is
sent anywhere real in development.

## Stack

```text
ASP.NET Core · Razor Pages + htmx · PostgreSQL 16 + pgvector · EF Core
Anthropic (fast + deep roles) behind ILLMProvider · docker compose

The application connects as a non-superuser role. A superuser connection
bypasses every row-level security policy and silently voids ADR 0005.
```

---

Private project. Personal use only.

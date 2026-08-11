# V0 Scope

Date: 2026-08-11
Status: Authoritative for the prototype. Supersedes SPEC.md §36 where they differ.

## The objective

> Two people can use Couple OS for one week through natural language and actually find it useful. — SPEC.md §53

V0 exists to test one hypothesis: **that typing what happened, in ordinary language, is a better way to run a shared household than opening five apps.** Everything below is either necessary to test that or is cut.

If V0 ships and the answer is no, almost none of the roadmap matters. That is the point of building it small.

---

## The contradiction this document resolves

SPEC.md §36 defines V0 as a tiny prototype and lists `search_memory` among its tools. SPEC.md §54 places embeddings, pgvector and semantic retrieval in Milestone 4, well after V0.

Taken literally, V0 depends on a milestone that comes after it.

**Resolution: `search_memory` stays in V0, implemented lexically.**

```text
V0    Postgres full-text search (tsvector) + trigram similarity (pg_trgm)
      over memories.content, filtered by type, visibility and recency.
      No embeddings. No embedding pipeline. No vector index.

M4    Add embedding generation, populate memories.embedding, switch
      retrieval to the hybrid strategy in SPEC.md §10.
```

Per ADR 0002 the `vector` extension is enabled and `memories.embedding` exists as a nullable column from the first migration, so M4 is a backfill rather than a migration. The column is simply unused in V0.

**Why this is the right cut.** The V0 question is whether *capture* is useful — whether a person will actually type "we're out of detergent" instead of opening a list app. Retrieval quality is the V1 question. Lexical search over a corpus of a few hundred memories, in a household with a small and repetitive vocabulary, is genuinely adequate; "Goa" and "detergent" are exact-match terms. Building an embedding pipeline to search 200 rows would be the clearest possible violation of SPEC.md §56.1.

---

## In scope

### Foundation
- ASP.NET Core API, single deployable (ADR 0001)
- PostgreSQL 16 with `vector`, `pg_trgm` extensions enabled
- EF Core migrations, `docker compose` for local development
- Structured logging, correlation IDs
- Row-level security on all couple-scoped tables (ADR 0005)

### Identity
- Magic-link sign-in (ADR 0007)
- Couple creation, partner invitation by email
- Session management, sign out
- Exactly two members per couple, enforced by constraint

### Capture — two surfaces, split by scope (ADR 0009)

**Shared: `shared.md`**
- Append-only text file both partners read and write
- Single-line quick-add box that appends to Inbox with no response
- Explicit `Process` action; blocks content-hashed so re-runs cannot duplicate
- Rewrites the file in place: Inbox / Needs your input / Processed
- **Change report** after every run — created, updated, superseded, failed
- Everything captured here is `shared_couple`, by construction

**Private: chat thread, one per partner**
- Conversational, immediate, in-band clarifying questions (SPEC.md §3.2)
- Where surprises, gifts and personal goals live (SPEC.md §19)
- Everything captured here is `private_user`, by construction
- `share_memory` is the only path from here to shared state, and it confirms

**Both surfaces**
- Intent classification against SPEC.md §7's enum
- Multi-action extraction (SPEC.md §25) with independent per-action execution
- Honest partial-failure reporting (SPEC.md §46)
- One tool pipeline; the surfaces differ in input and reporting, not in effect

### Attachments
- Upload, store, and link to whatever records the surrounding block produced
- **No OCR.** `ocr_status` exists so the pipeline arrives without a migration

### Tools — exactly seven
```text
create_task            create_reminder        create_memory
create_expense         create_event           create_shopping_item
search_memory (lexical)
```
Full contracts in `TOOLS.md`. Every tool validated, authorized, audited (ADR 0004).

### Data
- All V0 tables in `../data/schema.sql`
- Visibility on every row, defaulting to `private_user` when ambiguous
- `ai_actions` and `audit_logs` populated from the first commit — retrofitting an audit trail is how you end up without one

### Minimal read surface
- A flat list of what was captured, grouped by type, so the user can verify the system understood them
- The `Processed` section of `shared.md` doubles as this for shared items

---

## Explicitly out of scope

Cut, with the reason. Nothing here is cut because it is unimportant.

| Cut | Reason |
|---|---|
| Embeddings, pgvector retrieval | Solves V1's problem, not V0's. See above. |
| Dashboard (§35) | Needs accumulated data to be anything but empty. V1. |
| Notifications, proactive insights (§21, §27) | Cannot tune a threshold with no usage data. V1, per ADR 0008. |
| Chores and household module (§13) | Overlaps tasks; adds a second model before the first is validated. |
| Goals and plans (§17, §18) | Multi-step planning is the V2 hypothesis, not the V0 one. |
| Finance queries and aggregation (§15) | V0 records expenses; it does not answer questions about them. |
| Memory management UI (§30) | Required for V1 trust. For one week, the change report plus the archive section is enough. |
| Private dump file (a private notebook) | Private capture is mostly planning, which wants dialogue. `dump_files.kind='private'` exists in the schema, unused — addable without migration. |
| Real `.md` files synced from disk | Sync conflicts and a file watcher before the idea is validated. Export is cheap to add later. |
| Contradiction handling (§45) | Needs a corpus before conflicts exist. |
| Commitment tracking as a distinct type | Stored as `task_kind = 'commitment'`; no separate behaviour yet. |
| Agents (§26) | Explicitly V2. |
| All integrations (§39) | Explicitly V3. |
| Mobile app, voice, WhatsApp, OCR | Explicitly later. |
| Multi-couple, family mode | SPEC.md §4 defers it; the schema does not preclude it. |

---

## Deliberately deferred but designed for

These cost nothing now because the schema and architecture already accommodate them. Do not build them; do not make them expensive either.

- `memories.embedding` column exists, unused
- `dump_files.kind = 'private'` exists, unused — a private notebook needs no migration
- `attachments.ocr_status` exists, always `not_attempted` in V0
- `visibility` supports `system` scope, used only by inference rows
- `couple_members` is a join table, so family mode is a constraint change rather than a rewrite
- Tool contracts carry a `confirmation_tier` that V0 always reads as `none` for its seven tools
- `ILLMProvider` roles include `local`, unconfigured

---

## Definition of done

V0 is done when all of the following are true.

- [ ] Two people sign in via magic link and form a couple
- [ ] Both partners can write to `shared.md` and each sees the other's captures
- [ ] `Process` produces a change report naming every record created or updated
- [ ] Re-running `Process` on an unchanged file creates **nothing** (hash dedup)
- [ ] A private chat capture is never visible to the partner, on any surface
- [ ] Free-text input is classified into the correct intent for ≥90% of `data/eval-cases.jsonl` happy-path cases
- [ ] All seven tools execute, validate, authorize and audit correctly
- [ ] One message producing three actions results in three records, or in an accurate partial-failure message
- [ ] A private memory is never returned to the partner — verified by the adversarial cases in the eval set, not by inspection
- [ ] No tool call anywhere can set `visibility`; it is always inherited from the surface
- [ ] A failed tool call never produces success language in the response
- [ ] `search_memory` returns relevant results lexically over a seeded corpus of ≥100 memories
- [ ] Every AI mutation appears in `ai_actions` with input, output and outcome
- [ ] The full stack runs from a clean clone with `docker compose up` and one `.env` file

## Definition of *validated* — the harder bar

Done means it works. Validated means it is worth continuing.

After seven days of real use by two real people:

- [ ] ≥5 captures per day per couple, unprompted, across both surfaces
- [ ] Both partners actually write to `shared.md` — not one person doing all the capture
- [ ] ≥80% of captures need no correction
- [ ] At least one instance of the system surfacing something a person had genuinely forgotten
- [ ] Both users prefer it to the tool it replaced

If the last bullet is false, the correct response is to change the product, not to build V1.

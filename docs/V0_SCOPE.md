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
- ~~`share_memory` is the only path from here to shared state, and it confirms~~ — **not built; see the cut table below.** Left struck through rather than deleted, because three documents described it as present

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
| `share_memory` — promoting a private memory to shared | **Cut by accident and then noticed.** This document listed it in scope and ADR 0009 named it as the private thread's one exit; TOOLS.md tiers it V1, and V1 is where it stayed. Nothing was blocked on it because the seven registered tools all write to the surface they were called from, so no code ever wanted it. The consequence is one-directional and survivable for a week: private reads shared, shared never reads private, and now private cannot *promote* either — a plan worked out in the thread reaches the couple by being retyped. STATUS debt 49. |

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

Each box names what asserts it, because a ticked box with no named assertion is
an opinion. This list went four milestones without being touched — it was last
edited at M1, and the two boxes that were ticked were the two true then. What
follows is the reconciliation against what the suites actually assert; it came
out twelve of fourteen, and building the corpus the twelfth asked for made it
**thirteen. The one that does not hold is stated rather than rounded up.**

- [x] Two people sign in via magic link and form a couple
- [x] Both partners can write to `shared.md` and each sees the other's captures — `SharedFileEditorTests`: PartnerA writes, PartnerB reads it back in a fresh scope against real Postgres with RLS on, and a save against a stale version writes nothing
- [x] `Process` produces a change report naming every record created or updated — `CaptureRunTests.Every_block_is_accounted_for_including_the_one_that_produced_no_tool_call`, plus `dump-003` on the pipeline harness, where `change_report_must_list_all` requires a line per call and a count that matches
- [x] Re-running `Process` on an unchanged file creates **nothing** (hash dedup) — `CaptureRunTests.A_second_process_on_an_unchanged_file_creates_nothing`, and `dump-001` on the database harness, which also asserts the model was called once across two runs
- [x] A private chat capture is never visible to the partner, on any surface — `PrivateThreadIsolationTests` covers the assistant's turns as well as the user's (the leak M2 found), `privacy-003`/`injection-001`/`injection-004` cover memory search through the real query layer, `attach-002` covers attachments. Three surfaces exist and all three are asserted
- [x] Free-text input is classified into the correct intent for ≥90% of `data/eval-cases.jsonl` happy-path cases — `ExtractionEvals`, with the bar applied by `EvalGate.Thresholds` (`happy_path` → 0.90) rather than read by a person. Happy path scores 100% over three sampled attempts per case; none of the three `known_failure` cases is in this category. Needs a model provider configured, which is why `check.sh --fast` fails naming what it skipped
- [x] All seven tools execute, validate, authorize and audit correctly — in two halves, because one of the seven writes nothing. Execution and audit for the writers: `ToolPipelineTests.A_dispatched_call_writes_the_row_and_its_audit_entry` and `A_refused_call_writes_no_row_but_is_still_audited`. Validation and authorization generically over every `ITool`: `ToolDispatcherTests` refuses undeclared properties and any argument naming a scope, and audits every outcome including refusals. `search_memory` has no row to inspect, so its authorization is the RLS suite plus the privacy cases above rather than a write test
- [x] One message producing three actions results in three records, or in an accurate partial-failure message — `dump-003` for the three records, `multi-003` for the partial failure, run through the pipeline harness with the second call forced to fail and the rendered report read back
- [x] A private memory is never returned to the partner — verified by the adversarial cases in the eval set, not by inspection — `DatabaseEvals` runs `privacy-003`, `privacy-005`, `injection-001` and `injection-004` through the real query layer and asserts absence. The rest of the adversarial cases run on `extraction` only, so they judge what the model *proposed*, not what the database *returned*; the four above are the ones that close it
- [x] No tool call anywhere can set `visibility`; it is always inherited from the surface — `ToolCatalogueTests.No_tool_schema_offers_the_model_a_word_for_whose_data_it_is_writing` (no schema may name `visibility`, `couple_id`, `owner_user_id` or `user_id`) and `ToolDispatcherTests`, which fails a call carrying `visibility` whether or not a schema declared it. Two assertions because a schema can be edited and a dispatcher rule cannot be edited by accident
- [x] A failed tool call never produces success language in the response — `PrivateThreadTests` for the three shapes on the private surface (all refused, nothing at all, half succeeded), `multi-003`'s `response_must_not_claim_success_for` for the shared one. Per-scenario, not a scan of every render path
- [x] `search_memory` returns relevant results lexically over a seeded corpus of ≥100 memories — `MemoryCorpusFixture` seeds 104 clustered memories for a couple of its own and refuses to seed fewer; `MemorySearchRankingTests` asserts what comes back *first*. Ticked with two findings attached, because the corpus was built to look and it found something: STATUS debt 50 (the blend rewards brevity) and debt 51 (the trigram branch never fires). Both are characterised by passing tests named for what happens
- [ ] Every AI mutation appears in `ai_actions` with input, output and outcome — **two of three.** `Arguments` is the input and `Outcome` is the outcome, both asserted by `ToolPipelineTests`. The `result` jsonb column has no property on `AiAction` at all, so no output is ever stored — `entity_id` points at what was created, which is a pointer and not a record of what the tool returned. STATUS debt 48
- [x] The full stack runs from a clean clone with `docker compose up` and one `.env` file

## Definition of *validated* — the harder bar

Done means it works. Validated means it is worth continuing.

After seven days of real use by two real people:

- [ ] ≥5 captures per day per couple, unprompted, across both surfaces
- [ ] Both partners actually write to `shared.md` — not one person doing all the capture
- [ ] ≥80% of captures need no correction
- [ ] At least one instance of the system surfacing something a person had genuinely forgotten
- [ ] Both users prefer it to the tool it replaced

If the last bullet is false, the correct response is to change the product, not to build V1.

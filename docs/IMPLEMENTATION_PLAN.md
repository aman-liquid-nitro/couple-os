# V0 Implementation Plan

Date: 2026-08-11
Status: For review. No code written until this is approved.
Scope: [V0_SCOPE.md](./V0_SCOPE.md). Governed by ADRs 0001–0009.

---

> **This document is intent.** What has actually been built, and what is owed,
> lives in [STATUS.md](./STATUS.md). Milestone exit criteria are ticked here
> because they define the milestone; everything else about current state belongs
> there.

## Decisions taken at plan review

| Question | Decision |
|---|---|
| Backend | ASP.NET Core, as SPEC.md §41 and ADR 0001 specify. Reopened at the implementation boundary and re-confirmed. |
| Frontend | Razor Pages + htmx, one deployable. Supersedes §41's Next.js for V0 — see ADR 0010. |
| LLM provider | Ollama locally for development (ADR 0011); Anthropic for the validation run once a key exists. Selected by configuration. `embed` unconfigured; retrieval is lexical in V0. |
| Deployment | `docker compose` locally throughout. Where the live test runs is decided at M5, not before. |
| Delivery | Work lands on feature branches, written directly to the repo and reviewed by `git diff`. Nothing reaches `main` without an explicit merge. |
| First slice | Row-level security through EF Core, before anything is built on top of it. |
| Pace | Steady. Built properly, used when ready. Full test coverage and CI from M0. |

## Working agreement

```text
main                       reviewed and merged only
plan/v0-implementation     this document
m0/rls-through-efcore      next branch, opens after this is approved
```

One branch per milestone. Each ends with a passing test suite and a `git diff`
for review. A milestone is not done because the code exists — it is done when
its exit criteria are machine-checked.

---

## M0 · Walking skeleton

**The point of this milestone is to falsify one assumption**, and it is worth
being blunt about which: I verified the RLS policies directly against PostgreSQL,
and they hold. I have **not** verified that they survive EF Core.

The risk is specific. `SET LOCAL` is transaction-scoped, EF Core pools
connections, and a pooled connection carrying a stale `app.current_user_id`
would serve one partner's private data to the other. That failure is silent —
correct-looking results, wrong rows — and everything in M1–M5 sits on top of it.
If it is broken, better to know on day one than after the tool layer is written.

### Tasks

**Repository** — solution skeleton per SPEC.md §42: `CoupleOS.Api`
(Razor Pages + htmx, ADR 0010), `.Application`, `.Domain`, `.Infrastructure`,
`.AI`, `.Workers`, plus `tests/CoupleOS.{Unit,Integration,AI}Tests`. No `web/`
directory and no Node toolchain.

**Docker** — `docker compose` bringing up `postgres:16` (with `pgvector`),
`api`, `maildev`. One `.env.example`. A clean clone must reach a running stack
with one command.

**Database** — EF Core migration reproducing [`../data/schema.sql`](../data/schema.sql),
with a test asserting parity between the migration and the reference schema so
the two cannot drift silently.

**Two database roles, and this is not optional:**

```text
coupleos_owner   runs migrations, owns the tables
coupleos_app     the application's runtime role
                 NOT superuser, NOT BYPASSRLS  ← policies do not apply otherwise
```

`FORCE ROW LEVEL SECURITY` is already set on every policy-bearing table, so
policies apply to the owner too — but a superuser bypasses RLS unconditionally,
so the runtime role must be neither.

**Session context** — a scoped middleware opening a transaction per request and
setting the context as its first statement. Parameterised, because `SET LOCAL`
cannot take parameters and string interpolation here would be an injection hole
in the security mechanism itself:

```sql
SELECT set_config('app.current_user_id',   @userId,   true),
       set_config('app.current_couple_id', @coupleId, true);
--                                          ↑ true = transaction-local
```

**RLS is proven, not assumed.** Both halves of M0's original question are
already answered against PostgreSQL 16:

- `data/rls-tests.sql` — 30 assertions: read isolation, write path, connection
  reuse, prepared statements under `force_generic_plan`, privilege escalation.
- `data/rls-concurrency.sh` — 1600 transactions, 16 reused connections,
  `-M prepared` (Npgsql's mode), both couples interleaved 50/50. Zero leaks.
  This is M0's stated exit criterion at 16x the required volume.

Both harnesses are verified to fail when protection is removed, so a green run
means something. M0's remaining job is to port them to
`CoupleOS.IntegrationTests` and confirm EF Core's query filters compose with
these policies rather than fighting them.

Three defects the harness caught are fixed in `data/schema.sql`:
`audit_logs` leaked private memory bodies through `after_state`, and
`goal_transactions` and `plan_items` leaked free text to any couple because
"reachable only via a protected parent" is untrue of a direct SELECT.

The harness is verified in both directions: leaving `goal_transactions`
unprotected fails at D2, and over-protecting it fails at D6.

**Two rules the C# must honour**, both proven rather than assumed:
- Session variables are set with `SET LOCAL` inside the request transaction.
  Plain `SET` survives COMMIT and leaks the previous couple's rows to the next
  request on the same pooled connection. `DISCARD ALL` on pool return is the
  backstop.
- `UPDATE`/`DELETE` against a row hidden by policy is a silent no-op, not an
  error. Repositories must check affected-row counts and never infer success
  from the absence of an exception.

**One vertical slice** — seeded couple, no auth: a page with a text input that
posts to `/capture`, classifies with the `fast` role via `IAnthropicProvider`
behind `ILLMProvider`, calls `create_shopping_item`, writes an `ai_actions` row,
and swaps the result into the page with htmx.

### Exit criteria

- [x] `docker compose up` from a clean clone reaches a healthy stack, three containers — verified from an empty volume; `api` is no longer behind the `app` profile, and healthy means `/health` reached Postgres, not merely that a process started
- [x] The EF model agrees with `data/schema.sql`; parity test passes (ADR 0012 — SQL owns the schema, there is no migration)
- [x] The app connects as a role that is neither superuser nor `BYPASSRLS`
- [x] Partner B cannot read Partner A's private memory **through `DbContext`**
- [x] A request that fails to set session context returns **zero rows**, not all rows
- [x] **Pooling test: 100 interleaved requests alternating between Partner A and Partner B, asserting zero cross-contamination.** This is the test that actually matters — the single-request version passes even when pooling is broken.
- [x] One capture creates one shopping item and one `ai_actions` row
- [x] Eval harness exists and runs, with 3 cases wired in — 4 run today; `EvalCoverage` reports the 28 blocked on unregistered tools rather than letting them pass as coverage

> The harness ships here, not in M4. Gates written after the code they judge get
> written to pass.

---

## M1 · Identity

Magic links per ADR 0007. No passwords anywhere in the codebase.

- Token issue and consume: 32 CSPRNG bytes, SHA-256 at rest, 15-minute lifetime, single use consumed atomically
- Rate limiting: 3 per email per 15 min, 10 per IP per hour
- Identical response and timing whether or not the address exists
- Sessions: httpOnly / Secure / SameSite=Lax, 30-day rolling, revocable
- Couple creation and partner invitation on the same token mechanism
- Session context middleware switches from seeded IDs to the real session
- Dev mail to maildev; no external provider needed to run locally

### Exit criteria

Written as checks rather than the prose this section used to carry, for the same
reason M0's are: a milestone is done when its exit criteria are machine-checked.

- [x] Two real people in two browsers, each attributed to themselves — verified through the running container: two sessions, two `ai_actions` rows naming different users, one couple
- [x] Correct isolation — asserted through the couple scope a real session produces, not through fixtures: each partner sees shared rows and only their own private ones
- [x] Replay of a consumed token fails identically to an expired one — and to a token that never existed; all three are the same value, and `ConsumedToken` gives the caller no field to tell them apart
- [x] Single use is atomic — 12 simultaneous uses of one link, exactly one succeeds
- [x] Rate limits hold and recover — 3 per email per 15 min, 10 per IP per hour, verified at the boundary and after the window passes
- [x] An unknown address is answered identically to a known one — same outcome, same subject, bodies differing only in the token
- [x] The seeded-couple path is gone — `DevelopmentScopeMiddleware` and `DevelopmentSeeder` are deleted, not disabled

Anthropic API key lives in `.env`, never in the repo — `.gitignore` already
covers `.env` and `appsettings.Local.json`.

---

## M2 · Capture surfaces

Both surfaces from ADR 0009. Presentation differs; the pipeline does not.

**Shared — `shared.md`**
- File model, plain-textarea editor, `content_version` optimistic concurrency with a stale-version warning
- Quick-add: single line, appends to Inbox, no response, no state
- Segmentation into blocks, SHA-256 per block, `(dump_file_id, content_hash)` unique
- `Process`: classify → extract → execute → rewrite the file in place
- Change report persisted to `dump_runs.report`
- `Needs your input` parks clarifications without blocking other blocks

**Private — chat thread**
- One thread per partner, conversational, in-band clarification
- Every record `private_user`, forced by the pipeline, not chosen by the model

**Exit:** both surfaces produce blocks; a second `Process` on an unchanged file
creates nothing; a correction line supersedes rather than duplicates; **and the
change report accounts for every block, including those that produced no tool
call.**

> That last clause comes from running M0's slice. A three-line note produced two
> shopping items and a report listing exactly those two. The third line — an
> event — vanished: `create_event` was not registered, so the model had nothing
> to call and nothing to say, and the report describes what it *did* rather than
> what it *saw*. Nobody reading that screen would know a line had been ignored.
>
> Registering the missing tool would fix the example without fixing the defect.
> The report must account for the input, not the actions. Block segmentation is
> what makes that possible: every block carries a status, so silence becomes
> impossible to render.

> **On "both surfaces produce blocks", written after building them.** The private
> thread produces `conversation_messages`, not `dump_blocks`, and the difference
> is forced rather than chosen: `dump_blocks_dedup` is unique per file and
> content hash, so a repeated line in a conversation would collide with its
> earlier self and disappear. Repetition is noise in a dump file and meaning in a
> chat. The invariant this criterion is actually reaching for is the clause after
> it — one validate → authorize → execute → audit path — and that holds: one
> registry, one dispatcher, one audit sink, with `visibility` injected from the
> surface on both. STATUS.md records the reading; the wording here is left as it
> was written, because a plan edited to match what got built stops being a record
> of intent.

---

## M3 · The seven tools

`create_task` · `create_reminder` · `create_shopping_item` · `create_expense` ·
`create_event` · `create_memory` · `search_memory`

Per [TOOLS.md](./TOOLS.md) and ADR 0004. Each gets schema validation rejecting
unknown properties, an authorization predicate over the session, a confirmation
tier, an idempotency key, and an audit row on **every** call including failures.

`visibility` is injected from the capture surface. No tool accepts it. A test
asserts that no tool schema in the codebase contains the key.

Batch extraction prompt for shared, conversational loop for private, one
execution path underneath.

**Exit:** all seven work from both surfaces; a forced tool failure never
produces success language in the response.

---

## M4 · Eval gates

Harness exists from M0; this milestone fills it out and enforces it.

- [x] All 55 cases in [`../data/eval-cases.jsonl`](../data/eval-cases.jsonl) runnable
- [x] CI gate: **≥90% happy path, 100% privacy, 100% prompt injection, 100% idempotency**
- [x] Cases targeting V1 tools assert honest *unsupported* handling, not silence
- [x] Per-case cost and latency recorded, per SPEC.md §49

**Exit:** the V0 done-checklist is machine-checked. No model or prompt change
merges without a passing run (ADR 0003).

**Met.** Three harnesses, because the set states three kinds of property and one
cannot hold them: what a model proposed, what the pipeline rendered, what the
database let through. 66 case-harness runs. The gate is
`tools/CoupleOS.EvalGate`, and it fails on a category under its bar, on a result
naming a case the set does not declare, and on a case the set declares that no
harness reported — the last of which is what a gate needs to be one.

A prompt or tool-description edit fails the build until the request's digest is
updated deliberately, so a model getting worse and a schema being reworded are
no longer the same observation. See [STATUS.md](./STATUS.md) for what the run
actually says, including three cases that stay red and say why.

---

## M5 · Attachments and read surface

- [x] Upload, checksum, store, link to the entities its block produced
- [x] Attachments inherit their surface's scope; `ocr_status` stays `not_attempted`
- [x] Flat read view grouped by type, so a user can verify what was understood

**Exit:** V0 complete. One week of real use begins, measured against the
"Definition of validated" bar in [V0_SCOPE.md](./V0_SCOPE.md).

**Met.** Bytes on a named volume behind an interface ([ADR 0014](../decisions/0014-attachment-storage-on-the-filesystem.md)),
hashed while streaming, written under a temporary name and moved. Both surfaces
receive uploads and neither decides the scope from the file — the shared page
passes `shared_couple` and the private thread passes `private_user`, which is the
whole of ADR 0009's rule stated twice. The link carries the attachment's id
rather than its filename, so one implementation writes it and one reads it back,
and a Process links it to every record its block produced.

`/Captured` is the read surface: rows grouped by kind, private ones marked, no
totals anywhere. **V0 is complete.** What it is not is validated — that is what
the week of real use is for, and the bar it is measured against is in
V0_SCOPE.md rather than here.

---

## Risk register

| Risk | Milestone | Mitigation |
|---|---|---|
| RLS does not survive EF Core connection pooling | M0 | The 100-request interleaving test. Failing it stops everything until fixed. |
| Migration drifts from `schema.sql` | M0 | Parity test in CI |
| App accidentally runs as a superuser role in production | M0 | Startup assertion refusing to boot on a `BYPASSRLS` or superuser role |
| Batch extraction quality is worse than per-message | M2 | Eval harness comparison before committing to batch |
| Change report is not good enough to carry the feedback loop | M2 | It is the ADR 0009 risk; treat report quality as a feature, not output formatting |
| Mobile capture friction kills the V0 experiment | M5 | Quick-add is not optional |
| Tool list grows past what fits the context budget | M3 | Per-intent tool subsetting; deferred until measured |

---

## Explicitly not in this plan

Dashboard · notifications and proactive insights · chores · goals and plans ·
finance aggregation · memory management UI · contradiction handling · agents ·
integrations · embeddings and semantic retrieval · OCR · mobile app · voice.

All are scoped in [V0_SCOPE.md](./V0_SCOPE.md) with the reason for each cut.
None are cut because they are unimportant.

---

## Decisions still open

1. ~~**CI.**~~ **Settled at M4: both.** A remote exists now. `.github/workflows/ci.yml` runs the deterministic suite on every push and the model-dependent evals only when an `OLLAMA_API_KEY` secret is present — and when it is absent the gate job fails naming the cases that never ran, rather than reporting green for a third of a suite. `scripts/check.sh` and `check.ps1` run the same steps locally, in the same order, so the two agree by construction.
2. **Deployment target.** Deferred to M5 by decision. The candidates are a cloud VPS or self-hosting behind Tailscale; the latter fits SPEC.md §40's privacy stance and costs nothing.

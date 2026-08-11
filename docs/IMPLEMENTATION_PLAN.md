# V0 Implementation Plan

Date: 2026-08-11
Status: For review. No code written until this is approved.
Scope: [V0_SCOPE.md](./V0_SCOPE.md). Governed by ADRs 0001–0009.

---

## Decisions taken at plan review

| Question | Decision |
|---|---|
| Backend | ASP.NET Core, as SPEC.md §41 and ADR 0001 specify. Reopened at the implementation boundary and re-confirmed. |
| Frontend | Razor Pages + htmx, one deployable. Supersedes §41's Next.js for V0 — see ADR 0010. |
| LLM provider | Anthropic for `fast` and `deep` (ADR 0003). `embed` unconfigured; retrieval is lexical in V0. |
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

**RLS proven before anything is built on it** — `data/rls-tests.sql` already
exists and passes: 27 assertions run against PostgreSQL 16 covering read
isolation, the write path, connection reuse and privilege escalation. M0's job
is to port it to `CoupleOS.IntegrationTests` as xUnit and add the concurrency
dimension SQL cannot express — 100 interleaved requests across a pooled
connection. Three defects it already caught are fixed in `data/schema.sql`:
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

- [ ] `docker compose up` from a clean clone reaches a healthy stack, three containers
- [ ] Migration matches `data/schema.sql`; parity test passes
- [ ] The app connects as a role that is neither superuser nor `BYPASSRLS`
- [ ] Partner B cannot read Partner A's private memory **through `DbContext`**
- [ ] A request that fails to set session context returns **zero rows**, not all rows
- [ ] **Pooling test: 100 interleaved requests alternating between Partner A and Partner B, asserting zero cross-contamination.** This is the test that actually matters — the single-request version passes even when pooling is broken.
- [ ] One capture creates one shopping item and one `ai_actions` row
- [ ] Eval harness exists and runs, with 3 cases wired in

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

**Exit:** two real people in two browsers, correct isolation, replay of a
consumed token fails identically to an expired one.

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
creates nothing; a correction line supersedes rather than duplicates.

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

- All 55 cases in [`../data/eval-cases.jsonl`](../data/eval-cases.jsonl) runnable
- CI gate: **≥90% happy path, 100% privacy, 100% prompt injection, 100% idempotency**
- Cases targeting V1 tools assert honest *unsupported* handling, not silence
- Per-case cost and latency recorded, per SPEC.md §49

**Exit:** the V0 done-checklist is machine-checked. No model or prompt change
merges without a passing run (ADR 0003).

---

## M5 · Attachments and read surface

- Upload, checksum, store, link to the entities its block produced
- Attachments inherit their surface's scope; `ocr_status` stays `not_attempted`
- Flat read view grouped by type, so a user can verify what was understood

**Exit:** V0 complete. One week of real use begins, measured against the
"Definition of validated" bar in [V0_SCOPE.md](./V0_SCOPE.md).

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

1. **CI.** There is no git remote yet, so "CI gate" currently means a script you run. GitHub Actions on a private repo, or local-only until a remote exists?
2. **Deployment target.** Deferred to M5 by decision. The candidates are a cloud VPS or self-hosting behind Tailscale; the latter fits SPEC.md §40's privacy stance and costs nothing.

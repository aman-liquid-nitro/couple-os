# Architecture & Domain Model

Date: 2026-08-11
Status: Accepted for V0–V1. Governed by ADRs 0001–0008.

---

## 1. Shape

A modular monolith (ADR 0001). One deployable, one database, module boundaries enforced in code rather than over the network.

```text
                    ┌─────────────────────────┐
                    │  Browser · htmx, no SPA │
                    └────────────┬────────────┘
                                 │ HTTPS, cookie session, no CORS
                    ┌────────────▼────────────┐
                    │     CoupleOS.Api        │  Razor Pages, auth, view models
                    │     (ADR 0010)          │  JSON endpoints for later clients
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │  CoupleOS.Application   │  services, tool handlers,
                    │                         │  validation, authorization
                    └──┬───────────────────┬──┘
                       │                   │
          ┌────────────▼──────┐   ┌────────▼─────────────┐
          │  CoupleOS.Domain  │   │    CoupleOS.AI       │
          │  entities, rules  │   │  ILLMProvider, tool  │
          │  no dependencies  │   │  loop, prompts,      │
          └────────────┬──────┘   │  extraction          │
                       │          └────────┬─────────────┘
          ┌────────────▼───────────────────▼─────────────┐
          │           CoupleOS.Infrastructure            │
          │   EF Core · repositories · email · providers │
          └────────────────────┬─────────────────────────┘
                               │
                    ┌──────────▼──────────┐      ┌──────────────────┐
                    │  PostgreSQL 16      │◄─────┤ CoupleOS.Workers │
                    │  + vector + pg_trgm │      │ hosted service   │
                    └─────────────────────┘      └──────────────────┘
```

### Dependency rule

`Domain` depends on nothing. `Application` depends on `Domain`. `AI` depends on `Application` abstractions, never on `Domain` entities directly. `Infrastructure` depends on everything and is depended on by nothing except at composition root. An architecture test in `CoupleOS.UnitTests` asserts this; a violation fails the build.

**The AI layer is a client of the application, not a peer of it.** It cannot reach `Infrastructure`, cannot open a `DbContext`, and cannot construct a `Domain` entity. Its only verb is "call a tool" (ADR 0004).

---

## 2. Modules

| Module | Owns | V0 |
|---|---|---|
| Identity | users, couples, couple_members, sessions, auth_tokens | ✓ |
| Capture | dump_files, dump_blocks, dump_runs, attachments | ✓ |
| Chat | conversation_sessions, conversation_messages | ✓ private only |
| Memory | memories, memory_embeddings | ✓ lexical |
| Tasks | tasks (incl. commitments, reminders) | ✓ |
| Shopping | shopping_items | ✓ |
| Finance | expenses, expense_categories | ✓ record only |
| Calendar | events | ✓ |
| Household | chores | — |
| Goals | goals, goal_transactions | — |
| Planning | plans, plan_items | — |
| Notifications | notifications | — |
| Audit | ai_actions, audit_logs | ✓ |

Cross-module access goes through the owning module's application service interface. No module reads another module's tables directly.

---

## 3. Domain model

```text
User ──┐
       ├──< CoupleMember >── Couple ──┬──< DumpFile ──< DumpBlock ──< DumpBlockEntity
       │                              ├──< Attachment ──< AttachmentLink
User ──┘                              ├──< Memory
User ──┘                              ├──< Task ──────< (self: parent_task_id)
                                      ├──< ShoppingItem
                                      ├──< Expense >── ExpenseCategory
                                      ├──< Event
                                      ├──< Chore
                                      ├──< Goal ──────< GoalTransaction
                                      ├──< Plan ──────< PlanItem
                                      ├──< Notification
                                      ├──< ConversationSession ──< ConversationMessage
                                      └──< AiAction ──< AuditLog
```

### Universal columns

Every couple-scoped entity carries the same five columns. This is not boilerplate — it is the enforcement surface for ADR 0005.

```text
id                uuid, primary key
couple_id         uuid, not null
owner_user_id     uuid, null unless visibility = 'private_user'
visibility        visibility enum, not null
created_at        timestamptz, not null
```

Plus, where the entity is user-destroyable: `deleted_at timestamptz` for soft deletion with a 30-day recovery window (SPEC.md §56.13).

### Entity notes

**Task absorbs three concepts.** SPEC.md §11 distinguishes tasks from commitments and §7 treats reminders as a separate intent. All three share a title, an owner, a due date and a completion state. They are one table with a discriminator:

```text
task_kind:  task | reminder | commitment
```

A `commitment` differs by carrying `committed_to_user_id` and by being surfaced differently ("you said you would book the hotel"), not by having different storage. A `reminder` is a task whose `due_at` is required. This avoids three near-identical tables while preserving §11's semantic distinction.

**Memory is the richest entity**, per ADR 0006: `type`, `assertion`, `confidence`, `importance`, `subject_key`, `superseded_by_id`, `expires_at`, `embedding`.

**Expense never stores a computed total.** All aggregation is a query (SPEC.md §56.7). Amounts are `numeric(14,2)` with a separate `currency` — never floating point.

---

## 4. Request pipelines

Two entry points, one pipeline. Capture surface determines visibility (ADR 0009);
everything after step 5 is identical.

### 4a. Shared — `POST /api/dumps/{id}/process`

```text
Process action
  │
  ├─ 1  Authenticate            Api          → user_id, couple_id
  ├─ 2  Open transaction        Infra        SET LOCAL app.current_* (RLS armed)
  ├─ 3  Segment the file        Capture      split Inbox into blocks, hash each
  ├─ 4  Skip known hashes       Capture      ← re-processing cannot duplicate
  ├─ 5  Classify each block     AI  [fast]   → intent + confidence
  ├─ 6  Retrieve context        Application  memory + structured data, budgeted
  ├─ 7  Extract actions         AI  [deep]   one call for the whole batch
  ├─ 8  For each tool call      Application  validate → authorize → confirm-gate
  │                                          → execute → audit
  │                                          visibility := shared_couple, forced
  ├─ 9  Flag misfiled privacy   Capture      advisory only; never relocates text
  ├─ 10 Rewrite the file        Capture      Inbox / Needs your input / Processed
  ├─ 11 Persist the run         Capture      dump_runs.report = the change report
  ├─ 12 Commit                  Infra
  └─ 13 Enqueue extraction      Workers      memory candidates, post-response
```

The batch shape matters for cost: the `deep` model runs once per dump rather than
once per sentence (ADR 0003).

### 4b. Private — `POST /api/chat`

SPEC.md §43, made concrete. Each step names the component that owns it.

```text
POST /api/chat
  │
  ├─ 1  Authenticate            Api          session cookie → user_id, couple_id
  ├─ 2  Open transaction        Infra        SET LOCAL app.current_user_id / couple_id
  │                                          ← RLS is armed here (ADR 0005)
  ├─ 3  Persist user message    Inbox        conversation_messages
  ├─ 4  Classify intent         AI  [fast]   → intent enum + confidence
  ├─ 5  Retrieve context        Application  memory (lexical) + structured data,
  │                                          scoped by RLS, budgeted by token count
  ├─ 6  Build prompt            AI           system + context + last N turns
  ├─ 7  Completion              AI  [deep]   → text and/or tool calls
  ├─ 8  For each tool call      Application  validate → authorize → confirm-gate
  │                                          → execute → audit
  │                                          visibility := private_user, forced
  ├─ 9  Compose response        AI           from real tool outcomes only
  ├─ 10 Persist assistant msg   Inbox
  ├─ 11 Commit                  Infra
  └─ 12 Enqueue extraction      Workers      memory candidates, post-response
```

**Step 5 is where the product lives or dies.** Context selection is a budget, not a dump: newest-first within a token allowance, structured data before prose, hard cap on retrieved memories. SPEC.md §43 is explicit — do not send the entire database or the entire conversation history.

**Step 12 is deliberately after the response.** Memory formation must never add latency to the conversation (ADR 0006).

**Neither pipeline lets the model choose `visibility`.** It is set from the surface before any tool executes, exactly as `couple_id` is. This is what makes SPEC.md §19's surprise-gift case a structural guarantee instead of a prompt-quality problem.

---

## 5. Visibility, concretely

The single most important mechanism in the system (ADR 0005).

```sql
-- Once per request, inside the transaction, never on connection open:
SET LOCAL app.current_user_id   = '…';
SET LOCAL app.current_couple_id = '…';
```

```sql
-- Applied to every couple-scoped table:
CREATE POLICY couple_scope ON memories
  USING (
    couple_id = current_setting('app.current_couple_id')::uuid
    AND (
      visibility = 'shared_couple'
      OR (visibility = 'private_user'
          AND owner_user_id = current_setting('app.current_user_id')::uuid)
    )
  );
```

A developer who writes `context.Memories.ToList()` and forgets a visibility filter gets the correct rows anyway. That is the entire point: the private-data leak in SPEC.md §28 is prevented by the database, not by remembering.

`system`-scope rows are excluded from all user-facing policies and are readable only by the extraction pipeline's dedicated role.

`dump_files` carries the same policy, so a private thread and a shared file are separated by the same mechanism as the records they produce. Verified empirically: with three dump files seeded, Partner A's query returns `shared.md` plus her own file, Partner B's returns `shared.md` plus his, and neither sees the other's surprise.

---

## 6. Failure semantics

Non-negotiable, from SPEC.md §46 and §25.

| Situation | Behaviour |
|---|---|
| Tool validation fails | No write. Model told. Model asks the user for the missing field. |
| Tool throws | Transaction rolls back for that action only. Failure reported verbatim. |
| 2 of 3 actions succeed | Response states which two succeeded and which failed. Never "done!". |
| LLM provider times out | User message persisted, no partial state, retry offered. |
| Model claims a failed action succeeded | Caught by an assertion in the response layer, not by prompt instruction. |
| A block fails mid-dump | That block goes to `status = 'failed'` with its error; the rest of the run continues and the change report names it. |
| `Process` runs twice on one file | Second run is a no-op. `(dump_file_id, content_hash)` is unique. |
| Both partners edit `shared.md` at once | Last write wins, with a stale-version warning from `content_version`. |

The last row matters most. "Never claim an action happened if the tool failed" is a test in `CoupleOS.AITests`, because a rule enforced only by a prompt is a rule that fails silently under model change.

---

## 7. Deployment

```text
docker compose:  api  ·  postgres  ·  maildev (dev only)
```

One application container, not two — the UI is served by the API (ADR 0010).
No Node in the production image.

Single VPS. Managed Postgres or a volume-backed container. Nightly `pg_dump` to off-site object storage, encrypted — this database holds the only copy of things the couple has told no one else. Restore is tested, not assumed.

No Kubernetes, no service mesh, no message broker (SPEC.md §41).

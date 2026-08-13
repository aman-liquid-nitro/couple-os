# Tool Contracts

Date: 2026-08-11
Status: V0 tools are binding. V1/V2 tools are sketched, not final.
Governs: ADR 0004 (tool layer), ADR 0005 (visibility), ADR 0008 (confirmation tiers)

---

## The contract every tool honours

The LLM cannot write to the database. It can only emit a tool call, which passes through a fixed pipeline before anything is persisted (ADR 0004):

```text
validate → authorize → confirmation gate → execute → audit → return real outcome
```

Each tool declares five things:

| Field | Meaning |
|---|---|
| **Schema** | JSON Schema. Unknown properties rejected, not ignored. |
| **Authorization** | Predicate over the calling session. Never over the message text. |
| **Tier** | `none` · `confirm` · `authorize` (ADR 0008) |
| **Idempotent** | Whether a retry with the same key is safe |
| **Audit** | What lands in `ai_actions` / `audit_logs` |

### Visibility is not an argument

Per ADR 0009, capture happens on one of two surfaces, and the surface determines
the scope:

```text
shared.md (dump file)  →  visibility = shared_couple
private chat thread    →  visibility = private_user
```

The application injects `visibility` from the surface the input arrived on. **No
tool accepts it as a parameter.** It sits alongside `couple_id` and
`owner_user_id` on the list of values the model may never set — a model that
could set them could leak a surprise to the person it is for.

This removes the single hardest judgment in the original design. Earlier drafts
had `create_memory` inferring privacy from sentence shape; "she mentioned she
likes that bag" and "she mentioned she wants to visit her parents" are the same
shape with opposite answers, and one of those failures is unrecoverable.

### Universal rules

1. **Never invent a value to satisfy a required field.** A missing required field is a clarifying question to the user, not a guess. Inventing an amount, a date, or a payer is the failure mode SPEC.md §46 forbids outright.
2. **Visibility is inherited from the capture surface, never chosen** (ADR 0009). If gift-shaped text appears in the shared file, the run flags it and takes no action — the system does not relocate a user's words between scopes.
3. **Every call is audited, including failures.** A rejected call is as interesting as a successful one when debugging trust.
4. **Idempotency key** = `hash(conversation_message_id, tool_name, canonical_args)`. A retry after a timeout must not double-create.
5. **`couple_id` and `owner_user_id` are never accepted as arguments.** They come from the session. A model that could set them could impersonate.

### Shared argument shapes

```jsonc
// NOTE: no "visibility" key appears in any tool schema below. It is injected
// by the application from the capture surface (ADR 0009).

// Dates. The model emits intent; it does not do calendar arithmetic
// (non-negotiable 2, SPEC.md §56.7). So every schema below declares the
// EXPRESSION field, and the application resolves it against the couple's
// timezone and stores the timestamp.
"due_expression":  { "type": "string", "description": "verbatim, e.g. 'tomorrow', 'next friday', 'saturday 8pm'" }
"due_at":          // NOT a tool argument. The resolved column the application writes.
```

> **Why the expression is the declared field, and not a resolved `date-time`.**
> An earlier version of this section said dates are "resolved by the application
> before the tool is called", while every schema still declared `due_at` /
> `starts_at` as a `date-time`. That reads as two layers — a model-facing schema
> and a resolved tool contract — but there is only one: `ITool.ParametersSchema`
> is handed to the model verbatim (`CaptureProcessor`), and nothing sits between
> the model's arguments and the tool's. A `date-time` in a schema is therefore a
> `date-time` asked of the model.
>
> Measured, and this is the reason: given a schema asking for an ISO
> `starts_at`, three separate models resolved "Saturday 8pm" against their own
> training cutoffs and returned dates wrong by 7 months, 13 months, and nearly
> three years — no error, a plausible timestamp, a calendar entry in 2023. Given
> the same note with `date_expression`, all three returned "Saturday 8pm"
> verbatim. The field name is the whole control.
>
> Resolution is owed before the first date-bearing tool ships (M3), and it needs
> the couple's timezone, which `ToolExecutionContext` does not yet carry.

---

## V0 tools — the complete set

### 1. `create_task`

Tier `none` · idempotent · SPEC.md §11

```jsonc
{
  "title":       { "type": "string", "minLength": 1, "maxLength": 200 },   // required
  "description": { "type": ["string","null"], "maxLength": 2000 },
  "kind":        { "enum": ["task","commitment"], "default": "task" },
  "assigned_to": { "enum": ["me","partner","either"], "default": "either" },
  "due_expression": { "type": ["string","null"], "description": "verbatim, e.g. 'friday'" },
  "priority":    { "enum": ["low","normal","high"], "default": "normal" },
}
```

**Authorization** — caller is a member of the couple. `assigned_to: "partner"` resolves to the other member; it fails rather than guessing if the couple has one member.

**Notes.** `kind: "commitment"` requires a resolvable partner and records `committed_to_user_id`. "I said I would call my parents" is a commitment; "buy detergent" is a task. The distinction is social, not structural (SPEC.md §11).

> **`assigned_to` is not implemented, and the schema is why.** Written while
> building the tool. `tasks` has `owner_user_id` and `committed_to_user_id` and
> nothing else that names a person: the first is the privacy column — null unless
> the row is private (ARCHITECTURE.md §5), and read by the row-level security
> policy — and the second is tied to `kind = 'commitment'` by
> `tasks_commitment_needs_target`. SPEC.md §11's own model has no `assignedTo`
> either, so this argument originates here rather than in the spec. The choice was
> between adding a column mid-milestone under ADR 0012 and shipping the tool
> without the argument; the argument is **absent from the schema the model sees**,
> because offering a word the row cannot carry produces a model that believes it
> assigned work. The authorization sentence above still holds for the half that
> exists: a commitment resolves the other member and fails rather than guessing
> when there is only one. STATUS debt 35 owns the column.

---

### 2. `create_reminder`

Tier `none` · idempotent · SPEC.md §7

```jsonc
{
  "title":      { "type": "string", "minLength": 1, "maxLength": 200 },   // required
  "due_expression": { "type": "string", "description": "verbatim, e.g. 'friday 9am'" }, // required
  "for_whom":   { "enum": ["me","partner","both"], "default": "me" },
}
```

**Notes.** `due_expression` is required — this is the whole difference between a reminder and a task. If the user gives no time ("remind me to book the dentist"), the tool is **not** called. The model calls `request_clarification` instead. SPEC.md §3.2 shows exactly this case resolving to `Needs clarification: Yes`.

> Earlier wording here said "the model asks when", which assumed a conversation.
> In the dump-file flow (ADR 0009) there is nobody to ask in the moment, so
> asking must itself be a tool call — see `request_clarification` below.
> Verified against a local model: given no such tool, it correctly declined to
> invent a value, produced empty content, and the block silently vanished.

> **`for_whom` is not implemented either**, and for a different reason than
> `create_task`'s `assigned_to`: it describes who gets *notified*, and V0 has no
> notifications at all (V0_SCOPE.md cuts the delivery half). There is no column for
> the intent and no mechanism for the effect, so it is absent from the schema rather
> than accepted and ignored. Same debt, STATUS 35.
>
> **What the tool does say out loud.** The resolved time is reported back with
> anything that was filled in: *"friday" read as Fri 14 Aug 2026, 09:00 — assumed
> 9am, since no time was given*. That sentence is a tool-result field
> (`ToolExecution.Note`) rather than prose, so the change report prints it on the
> line it belongs to. Nine in the morning and not midnight, because midnight is
> what any "just take the date" implementation produces and a reminder at midnight
> is a reminder nobody sees.

---

### 2a. `request_clarification`

Tier `none` · idempotent · SPEC.md §3.2, §46 · ADR 0009

```jsonc
{
  "question": { "type": "string", "maxLength": 300 },   // required, one specific question
  "about":    { "type": "string", "maxLength": 300 },   // required, the fragment in question
}
```

**Authorization** — caller is a member of the couple. Writes nothing except the
block's own `status` and `question`.

**Notes.** This is the only legal way to decline. Rule 1 of this document
forbids inventing a value to satisfy a required field, and ADR 0004 makes tools
the sole write path — so without this tool a model facing a missing value has no
compliant action available, and the block disappears with no record and no
error. It sets `dump_blocks.status = 'needs_input'` and fills `question`, which
the schema has always had columns for; this is the tool that populates them.

Not for calendar arithmetic. "Saturday 8pm" is not missing information — it is a
`date_expression` the application resolves. Calling `request_clarification` for a
resolvable date is a failure, and the eval set should assert against it.

**Built.** The two arguments are composed into ADR 0009's `"fragment" —
question` by the tool, because the model is the only thing that knows which half
of a three-item note is in doubt. The tool itself writes nothing: it returns the
question, and the block pipeline is what sets `status` and `question`. An open
question outranks a success in the same block, so a line that both added
something and asked something parks rather than being archived as done — see
STATUS debt 31 for what that trade costs.

---

### 3. `create_shopping_item`

Tier `none` · idempotent · SPEC.md §12

```jsonc
{
  "name":       { "type": "string", "minLength": 1, "maxLength": 100 },   // required
  "quantity":   { "type": ["string","null"] },
  "category":   { "type": ["string","null"] },
}
```

**Notes.** The canonical happy path (SPEC.md §24). Deduplicates against `normalized_name` among items with `status = 'needed'` — "we're out of detergent" twice in a day is one item, and the response says so rather than silently creating a second. Never sets `estimated_consumption_days`; that is learned by a background job from purchase history, and remains advisory (SPEC.md §12).

---

### 4. `create_expense`

Tier `none` · idempotent · SPEC.md §14

```jsonc
{
  "amount":      { "type": "number", "exclusiveMinimum": 0 },             // required
  "currency":    { "type": "string", "pattern": "^[A-Z]{3}$", "default": "INR" },
  "description": { "type": ["string","null"], "maxLength": 500 },
  "category":    { "type": ["string","null"] },
  "merchant":    { "type": ["string","null"] },
  "paid_by":     { "enum": ["me","partner","unknown"], "default": "me" },
  "is_shared":   { "type": "boolean", "default": true },
  "occurred_expression": { "type": ["string","null"], "description": "verbatim, e.g. 'yesterday'; defaults to today" },
}
```

**Validation.** `amount` is parsed to `numeric(14,2)` in application code, never by the model. "₹2400", "2.4k", "2,400" are normalized before the tool sees them; anything unparseable is a clarifying question, never a guess.

**Notes.** SPEC.md §14 is explicit — *if the payer is ambiguous, ask*. `paid_by: "unknown"` is therefore a valid model output that triggers a follow-up question rather than a default to the speaker. A gift expense should be `private_user`, and the model is instructed to treat gift-shaped purchases as private by default (SPEC.md §19).

---

### 5. `create_event`

Tier `none` · idempotent · SPEC.md §16

```jsonc
{
  "title":       { "type": "string", "minLength": 1, "maxLength": 200 },  // required
  "date_expression": { "type": "string", "description": "verbatim, e.g. 'saturday 8pm'" }, // required
  "end_expression":  { "type": ["string","null"], "description": "verbatim, e.g. 'until 11'" },
  "all_day":     { "type": "boolean", "default": false },
  "location":    { "type": ["string","null"] },
  "category":    { "enum": ["birthday","anniversary","appointment","trip",
                            "family","bill","renewal","other"], "default": "other" },
  "recurrence":  { "enum": ["none","yearly","monthly","weekly"], "default": "none" },
}
```

**Notes.** Birthdays and anniversaries default to `recurrence: "yearly"` and `all_day: true`. A year-less date ("her birthday is September 12") resolves to the next occurrence, and the response states the year it assumed so a wrong guess is correctable.

The application resolves `date_expression` into the `starts_at` / `ends_at`
columns; the model never sees those. `events.starts_at` stays `NOT NULL` in
`data/schema.sql` — the resolver, not the model, is what guarantees it, and a
resolver that cannot parse the expression must fail the call rather than pick a
date. `request_clarification` covers the case where the note genuinely has no
time in it; "saturday 8pm" is not that case (see 2a).

---

### 6. `create_memory`

Tier `none` · **not** idempotent (dedup is semantic, not key-based) · SPEC.md §8, ADR 0006

```jsonc
{
  "content":    { "type": "string", "minLength": 1, "maxLength": 2000 },  // required
  "type":       { "enum": ["episodic","semantic","preference","decision",
                           "commitment","plan","event","temporary_context"] }, // required
  "assertion":  { "enum": ["user_stated","inferred"], "default": "user_stated" },
  "confidence": { "type": "number", "minimum": 0, "maximum": 1, "default": 1.0 },
  "subject_key":{ "type": ["string","null"] },
  "expires_expression": { "type": ["string","null"], "description": "verbatim, e.g. 'for two weeks'" },
}
```

**Validation.** `type: "temporary_context"` requires `expires_expression`; the application resolves it into the `expires_at` column and defaults it to 30 days rather than rejecting (ADR 0006). `assertion: "inferred"` forces `confidence <= 0.7` regardless of what the model claims — a model cannot certify its own guess.

**Notes.** Visibility is structural, not inferred (ADR 0009). "She mentioned she really likes that bag" typed into the private chat thread is a `private_user` memory because of where it was typed. Typed into `shared.md`, it becomes a shared memory *and* raises `dump_blocks.privacy_flagged`, so the change report says "this looks like a surprise and it is in the shared file" — advisory only. The user's choice of surface is authoritative.

---

### 7. `search_memory`

Tier `none` · read-only · SPEC.md §10, docs/V0_SCOPE.md

```jsonc
{
  "query":      { "type": "string", "minLength": 1 },                     // required
  "types":      { "type": "array", "items": { "enum": [ /* memory_type */ ] } },
  "since_expression": { "type": ["string","null"], "description": "verbatim, e.g. 'since june'" },
  "limit":      { "type": "integer", "minimum": 1, "maximum": 20, "default": 10 }
}
```

**V0 implementation is lexical**: `tsvector` full-text plus `pg_trgm` similarity, ordered by a blend of match rank, recency and importance. No embeddings (see V0_SCOPE.md). The signature does not change at Milestone 4 — only the ranking behind it.

**Authorization is not a parameter.** Scope comes from RLS (ADR 0005) and from the calling surface (ADR 0009), so there is no `visibility` filter to pass and no way for a crafted query to widen it. A search issued from `shared.md` sees only shared rows; a search from a private thread sees that partner's private rows plus shared ones. A memory the caller may not see cannot be a candidate, let alone a result.

---

## V1 / V2 tools — sketched

Contracts firm up when their milestone starts. Tiers are already decided.

| Tool | Tier | Notes |
|---|---|---|
| `update_task` | `none` | Status and field changes on an existing task |
| `complete_task` | `none` | Idempotent; completing a done task is a no-op, not an error |
| `complete_shopping_item` | `none` | Matches on `normalized_name` among `needed` items |
| `query_expenses` | `none` | **Typed aggregation only.** Fixed shapes: by period, by category, by payer. No free-form SQL, no LLM arithmetic (SPEC.md §56.7). Returns figures the model explains but never computes. |
| `search_events` | `none` | Date-range and category filtered |
| `search_decisions` | `none` | Thin wrapper over `search_memory` with `types: ["decision"]` |
| `search_people` | `none` | V2; needs a people model that does not exist yet |
| `create_goal` | `confirm` | Financial commitment — SPEC.md §47 puts this in the middle tier |
| `update_goal` | `confirm` | Amount changes are confirmed; SPEC.md §17's "we saved another ₹20k" updates only when the target goal is unambiguous, otherwise it asks which |
| `create_plan` | `none` | Creating a plan is cheap and reversible; its child tasks inherit their own tiers |
| `delete_memory` | `confirm` | Soft delete, 30-day recovery |
| `share_memory` | `confirm` | The **only** path from a private thread to shared state. Warned as effectively irreversible (ADR 0005, 0009) |
| `delete_context` | `authorize` | SPEC.md §31. States the exact count from a real query before proceeding |

---

## What deliberately has no tool

Absence here is a design decision, not an omission.

- **`execute_query` / natural-language SQL** — ADR 0004. The single largest privacy and correctness risk in the system.
- **`send_message` / `send_email`** — nothing leaves the system in V0–V2. SPEC.md §47 puts external sends in the `authorize` tier; the safest version is not building it.
- **`update_memory_confidence`** — confidence is set by the extraction pipeline under deterministic rules, not by conversational assertion. Otherwise "trust me, you're sure about this" becomes an attack.
- **`create_user` / `add_couple_member`** — identity changes are never AI-initiated.
- **Any tool taking `couple_id`, `user_id`, or `visibility`** — these come from the session and the capture surface. Always.
- **A tool that moves content between privacy scopes automatically** — `share_memory` is user-initiated and confirmed. Nothing relocates a user's words on the AI's judgment (ADR 0009).

---

## Testing

Every tool needs, at minimum:

1. **Schema rejection** — malformed and extra arguments are refused, not coerced.
2. **Authorization** — a caller outside the couple gets nothing, verified against RLS rather than mocked.
3. **Visibility inheritance** — the same sentence on each surface produces the correct scope, and no tool call can override it.
4. **Idempotency** — the same key twice produces one record.
5. **Failure honesty** — a forced failure never yields success language in the response.

Cases live in `../data/eval-cases.jsonl` and run in `CoupleOS.AITests`.

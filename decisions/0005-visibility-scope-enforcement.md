# 0005. Three-value visibility, enforced at the database

Date: 2026-08-11
Status: Accepted

## Context

The specification contradicts itself. §9 gives `Memory.visibility` two values, `PRIVATE` and `SHARED`. §28 defines three data scopes, `PRIVATE_USER`, `SHARED_COUPLE` and `SYSTEM`. Implementing both produces a mapping bug in exactly the place the product cannot afford one.

§28 also states the harder requirement: *never retrieve private partner data just because it is semantically relevant*, with authorization ahead of retrieval, ahead of the LLM context. §19 supplies the motivating case — a gift idea overheard and stored privately must not surface in a shared answer.

Application-layer filtering alone is insufficient. It takes one query written without a `WHERE visibility` clause, in one code path, to leak a surprise gift to the person it was for. That failure is silent, permanent and relationship-damaging.

## Decision

**One enum, three values**, used by every table holding couple data. §9's two-value list is superseded.

```sql
CREATE TYPE visibility AS ENUM ('private_user', 'shared_couple', 'system');
```

- `private_user` — visible only to `owner_user_id`. Never enters the partner's context under any retrieval path.
- `shared_couple` — visible to both members of `couple_id`.
- `system` — internal rows (inferences pending confirmation, telemetry, derived aggregates). Never enters an LLM prompt directly and is never shown as fact.

**Every couple-scoped table carries `couple_id`, `owner_user_id` and `visibility`**, with a constraint making the combination meaningful:

```sql
CONSTRAINT owner_required_when_private
  CHECK (visibility <> 'private_user' OR owner_user_id IS NOT NULL)
```

**Enforcement is at the database, not in application code.** PostgreSQL row-level security is enabled on every couple-scoped table. The application connects as a non-superuser role and sets `app.current_user_id` and `app.current_couple_id` per request; policies do the rest. Application-layer filtering still exists, but as the second line, not the only one. A developer who forgets a `WHERE` clause gets an empty result, not a leak.

**Visibility transitions mutate the row in place.** Sharing a private memory updates `visibility` and stamps `visibility_changed_at`; it does not create a second row. Rationale: memory identity must be stable so embeddings, plan references and audit entries do not fragment. The transition is written to `audit_logs`, and the UI must warn that sharing is effectively irreversible — the partner cannot un-see it.

**Default is `private_user` when ambiguous.** If the AI cannot determine intended visibility with confidence, it stores privately and asks. §52.3 ranks privacy above convenience; the recoverable error is the private one.

## Consequences

**Easier.** The semantic-retrieval leak in §28 becomes structurally impossible rather than a code-review responsibility. Any new table inherits the pattern. Security review has one place to look.

**Harder.** RLS requires disciplined connection management — a pooled connection carrying a stale `app.current_user_id` is a serious bug, so the session variable is set inside the same transaction as the query, never on connection open. Debugging is less obvious when rows vanish for policy reasons; developer tooling needs an explicit, audited bypass role that is unavailable in production. Migrations must remember to enable RLS on new tables, which an integration test asserts.

**Accepting.** A performance cost on every query, and one more concept for a future contributor to learn.

## Alternatives considered

**Two values per §9** — rejected. Leaves system-generated inferences with nowhere to live except mixed in with user facts, which §9 itself warns against ("AI inference should not automatically be treated as confirmed fact").

**Application-layer filtering only** — rejected. One forgotten predicate is one irreversible leak. §28 calls privacy a first-class *architectural* requirement, and architecture means the structure prevents it.

**Separate tables for private and shared data** — rejected. Doubles every query, and moving a memory between scopes becomes a cross-table migration that breaks foreign keys.

# 0004. The tool layer is the only path from LLM to state

Date: 2026-08-11
Status: Accepted

## Context

§3.1 rejects direct database manipulation by the LLM and prescribes a tool/API layer with validation in front of application services. §56.8 requires treating AI output as untrusted input. §56.6 forbids the LLM being the source of truth for structured data. §46 forbids claiming an action happened when the tool failed.

These are one architectural commitment stated four times.

## Decision

The LLM cannot write. It can only request writes by emitting a tool call, and every tool call passes through a fixed pipeline before anything is persisted:

```text
LLM tool call
    ↓  schema validation      reject malformed args, no coercion of ambiguity
    ↓  authorization          caller may act on this couple, this entity, this scope
    ↓  confirmation gate      per 0008 tier; pause and ask if required
    ↓  application service    domain logic, invariants, deterministic calculation
    ↓  database               inside a transaction
    ↓  audit entry            ai_actions + audit_logs, always, success or failure
    ↓  result to LLM          the real outcome, including failure
```

Every tool declares a strict JSON schema, an authorization predicate, a confirmation tier, and whether it is idempotent. Contracts live in `docs/TOOLS.md`.

Three rules are non-negotiable:

1. **No raw SQL from the AI layer, ever.** No `execute_query` tool, no natural-language-to-SQL. Finance questions (§15) go through typed query tools with fixed shapes and deterministic aggregation in application code, never through LLM arithmetic (§56.7).
2. **Tool results are returned verbatim.** A failed tool returns its failure to the model, and the model must surface it. The response layer asserts that no success language is emitted for a failed call — this is a test, not a prompt instruction.
3. **Partial failure is reported as partial.** §25's multi-action extraction executes each action independently; if two of three succeed, the user is told exactly that (§25, §46).

Create tools accept an idempotency key derived from `(conversation_message_id, tool_name, argument_hash)`, so a retried call after a timeout cannot double-create an expense.

## Consequences

**Easier.** Prompt injection cannot escalate beyond the tools the current user is already authorized to call, because authorization is evaluated against the session, not against the message. Every AI mutation is replayable from `ai_actions`, which §32 requires for debugging trust issues. The domain layer stays testable with no LLM in the loop.

**Harder.** Every new capability needs a hand-written tool with a schema and tests — deliberately more friction than letting the model improvise. Tool count grows, and the tool list itself consumes context, which will eventually force per-intent tool subsetting.

**Accepting.** The model cannot do anything the product has not explicitly enabled. This is the trade §52.2 asks for: trust over autonomy.

## Alternatives considered

**LLM writes SQL against a restricted role** — rejected. Row-level security would be the only barrier between a prompt injection and the partner's private data, schema changes silently change model behaviour, and §32's audit trail degrades to a query log.

**LLM calls the public REST API over HTTP** — rejected. Adds a network hop inside the process, duplicates authorization, and §34 explicitly prefers internal services over arbitrary HTTP endpoints.

**Free-form JSON output parsed by the application** — rejected. This is a tool layer with worse validation and no type safety.

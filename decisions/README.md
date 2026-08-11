# Architecture Decision Records

One file per decision: `NNNN-short-title.md`.

| # | Decision | Status |
|---|---|---|
| [0001](./0001-modular-monolith.md) | Modular monolith over microservices | Accepted |
| [0002](./0002-postgresql-pgvector.md) | PostgreSQL with pgvector, no dedicated vector database | Accepted |
| [0003](./0003-llm-provider-abstraction-and-model-routing.md) | Provider abstraction with role-based model routing | Accepted |
| [0004](./0004-tool-layer-as-sole-llm-write-path.md) | The tool layer is the only path from LLM to state | Accepted |
| [0005](./0005-visibility-scope-enforcement.md) | Three-value visibility, enforced at the database | Accepted |
| [0006](./0006-memory-taxonomy-and-confidence.md) | Memory taxonomy, confidence, and the inference boundary | Accepted |
| [0007](./0007-authentication-magic-links.md) | Magic-link authentication for V1 | Accepted |
| [0008](./0008-notification-usefulness-threshold.md) | Notification usefulness threshold and confirmation tiers | Accepted |
| [0009](./0009-capture-surfaces-split-by-scope.md) | Two capture surfaces, split by visibility scope | Accepted |
| [0010](./0010-server-rendered-ui-for-v0.md) | Server-rendered UI with htmx for V0 | Accepted |

## Template

````markdown
# NNNN. Title

Date: YYYY-MM-DD
Status: Proposed | Accepted | Superseded by NNNN

## Context
What forces are at play? What constraint or question prompted this?

## Decision
What we are doing.

## Consequences
What becomes easier. What becomes harder. What we are accepting.

## Alternatives considered
Option — why rejected.
````

## Candidates not yet written

- Archive rollover policy for `shared.md` once it grows past a few thousand lines
- Context selection and token budgeting strategy for the §43 pipeline
- Recurrence representation (RRULE vs. a custom model) across tasks, chores and events
- Timezone and locale handling for a couple who may not share one
- Backup, restore and export format for §31's data-deletion and export requirements

# Architecture Decision Records

One file per decision: `NNNN-short-title.md` (e.g. `0001-modular-monolith.md`).

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

## Candidate ADRs from the spec

- 0001 — Modular monolith over microservices (§56.2, §57)
- 0002 — PostgreSQL + pgvector over a dedicated vector DB (§33)
- 0003 — LLM provider abstraction and model routing policy (§22, §50)
- 0004 — Tool layer as the only LLM write path (§3.1, §23)
- 0005 — Visibility scope enforcement at the retrieval layer (§28)
- 0006 — Memory type taxonomy and confidence model (§8, §9)
- 0007 — Authentication method for V1 (§41)
- 0008 — Notification usefulness threshold (§21, §27)

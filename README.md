# Couple OS

A private, intelligent memory and coordination layer for a couple's everyday life.

**Status:** Specification complete. Not yet implemented. First target is V0 (spec §36).

## Where things live

| Folder | Contents |
|---|---|
| `docs/` | Canonical specification and architecture documents |
| `product/` | User stories, flows, module briefs, UI copy |
| `design/` | Wireframes, dashboard mockups, visual design |
| `data/` | Schema drafts, seed data, AI evaluation datasets |
| `research/` | Model/provider comparisons, cost analysis, prior art |
| `decisions/` | Architecture Decision Records (ADRs) |
| `assets/` | Images, exports, screenshots |

## Start here

- [docs/SPEC.md](./docs/SPEC.md) — full product and technical specification

## Core principle

Capture → Understand → Remember → Organize → Act → Learn

## Non-negotiables (spec §52, §56)

1. The LLM is never the source of truth for structured data.
2. All calculations are deterministic application code, not LLM arithmetic.
3. Every record carries a visibility scope; private data never enters shared context.
4. Never claim an action succeeded when the tool failed.
5. Prove the core natural-language interaction is useful before building outward.

---

Private project. Personal use only.

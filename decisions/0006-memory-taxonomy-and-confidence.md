# 0006. Memory taxonomy, confidence, and the inference boundary

Date: 2026-08-11
Status: Accepted

## Context

§8 lists eight kinds of memory — episodic, semantic, preference, decision, commitment, plan, important event, temporary context — and warns that not all conversation should become permanent memory. §9 requires confidence and importance metadata and states the load-bearing rule: **AI inference should not automatically be treated as confirmed fact**. §45 requires contradictions to supersede rather than accumulate silently.

The failure mode is specific and corrosive. "I *think* she prefers Italian food" becomes, three months later, "You prefer Italian food" stated as fact, and the user loses trust in every other thing the system claims to remember. §46 forbids fabricating memories; a low-confidence inference promoted to fact is fabrication with extra steps.

## Decision

**Type is a closed enum**, matching §8 exactly:

```text
episodic | semantic | preference | decision | commitment | plan | event | temporary_context
```

**Confidence is two fields, not one.** A float alone cannot distinguish "the user said this" from "the model guessed well".

```text
assertion   user_stated | user_confirmed | inferred | imported
confidence  numeric(3,2), 0.00-1.00
```

The rule that follows is absolute: **only `user_stated` and `user_confirmed` memories may be presented as fact.** An `inferred` memory may be surfaced, but only as a hypothesis with its provenance attached — "I noted you might prefer Italian food, from a conversation in March. Is that right?" — and confirming it flips `assertion` to `user_confirmed`, records the confirming message, and raises confidence. This satisfies §46's requirement to distinguish remembered information from inference.

**`temporary_context` requires an expiry.** §8 warns that "we are currently comparing washing machines" must not become permanent. `expires_at` is `NOT NULL` for this type, defaulting to 30 days, and expired rows leave the retrieval pool automatically. A background job archives rather than deletes, so the audit trail survives.

**Importance is assigned by rule, not by the model.** Anniversaries, birthdays and explicit commitments are high by type. Model-suggested importance is capped so a persuasive sentence cannot promote itself to the top of every retrieval.

**Contradictions supersede, per §45.** On extraction, a candidate is checked against existing memories of the same type and subject. On conflict the old row's `status` becomes `superseded`, `superseded_by_id` points at the new row, and the new row inherits the subject. Nothing is silently deleted and nothing contradictory stays live. "Likes coffee" followed by "doesn't drink coffee anymore" leaves one live memory and one auditable history.

**Extraction is a separate, cheap pass.** Memory candidates are extracted after the response is delivered (§44), using the `fast` role from 0003, so memory formation never adds latency to the conversation.

## Consequences

**Easier.** §30's memory management UI has everything it needs to render honestly — source, confidence, assertion type, visibility, and an edit/forget control. Users can audit what the system believes and why, which is the foundation of §52.7's user control over AI assumptions.

**Harder.** More metadata per memory and a more complex extraction pipeline. Deduplication and conflict detection need the subject to be resolvable, which means a canonical `subject_key` on preference and semantic memories — imperfect, and a known source of near-duplicate rows. The eval suite covers this case explicitly.

**Accepting.** The system will sometimes ask a confirming question a more confident product would skip. That friction is the price of a memory the user can trust, and §52.10 ranks reliable memory above everything it competes with.

## Alternatives considered

**Single confidence float** — rejected. Cannot separate provenance from certainty, so a high-confidence guess is indistinguishable from a direct quote at the point where it matters.

**Store everything, filter at retrieval** — rejected. §8 opens by warning against exactly this, and it makes §31's "forget everything about our Goa trip" unbounded.

**Let the LLM decide importance and expiry freely** — rejected under §56.8. Model-assigned importance is advisory input to a deterministic rule, not the rule itself.

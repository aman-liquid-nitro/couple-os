# 0008. Notification usefulness threshold and confirmation tiers

Date: 2026-08-11
Status: Accepted

## Context

§21 requires notifications to be useful and low-noise and states that the AI must not generate unlimited notifications. §27 is more precise: candidate insights are ranked by usefulness, and *a notification should pass a usefulness threshold* — do not notify merely because something changed. §52.6 lists low notification noise among the product principles.

This is the feature most likely to kill the product. A proactive assistant that pings four times a day gets muted in a week, and a muted assistant is a dead one. The daily job in §27 will happily generate a dozen technically-true insights every morning.

§47 poses a related question with the same shape: which actions need confirmation. Both are "how much should the system interrupt" problems, so they are decided together.

## Decision

### Notification scoring

Every candidate insight is scored deterministically in application code — the model may draft the wording, never the priority.

```text
score = urgency × usefulness × novelty × actionability

urgency        1.0 due within 24h · 0.7 within 3 days · 0.4 this week · 0.1 beyond
usefulness     1.0 user can act now · 0.5 needs planning · 0.2 informational only
novelty        1.0 not seen · 0.3 seen once · 0.0 seen twice or dismissed
actionability  1.0 concrete next step exists · 0.4 vague · 0.0 nothing to do
```

Delivery requires `score >= 0.5`, and hard caps apply regardless of score:

```text
Proactive notifications   max 3 per couple per day, max 10 per week
Deduplication             7-day window per (type, entity_id)
Dismissal                 twice dismissed for a type ⇒ suppressed 30 days
Quiet hours               default 22:00-08:00 couple-local, queued not dropped
Prediction confidence     predictions (§12) below 0.7 are never notified
```

Deadline-bound facts — bill due, anniversary, appointment — bypass the cap only inside 24 hours, because a missed anniversary is worse than a redundant ping.

Dismissals are training data. Every dismissal writes to `notifications.dismissed_at`, and §49's notification-dismissal metric is the health signal for this whole subsystem: a dismissal rate above 30% means the threshold is wrong.

### Confirmation tiers

§47's three tiers are made concrete, and every tool in `docs/TOOLS.md` declares one.

```text
none        Add shopping item · create task · create note · low-risk memory
            Reversible, single-entity, no financial or social consequence.

confirm     Create or modify a financial goal · delete a memory · modify an
            existing event · change a shared preference · share a private memory
            Shown as a summary the user accepts before execution.

authorize   Delete more than 5 entities · delete_context (§31) · send anything
            outside the system · external purchases · change account settings
            Requires explicit typed or re-authenticated confirmation, states the
            exact count and scope, and is always logged.
```

Two rules cut across the tiers. Destructive operations are soft-deleted with a 30-day recovery window (§56.13), so `authorize` protects against mistakes that remain undoable for a month. And per §46, the confirmation prompt must state the real count — "this will permanently delete 184 shared memories" — computed by a query, never estimated by the model.

## Consequences

**Easier.** Notification volume is bounded by construction, so no prompt change can flood the user. The dismissal metric gives a single number to tune against. Confirmation policy lives in tool metadata rather than in prompts, so it cannot be talked around by a persuasive message.

**Harder.** Threshold constants are guesses until there is real usage; they live in configuration, not in code, and are expected to move. Suppression means genuinely useful notifications will occasionally be withheld — the caps favour silence, and a missed prompt is the failure mode being chosen deliberately. Scoring also requires per-couple notification history, so the daily job reads more than the current day.

**Accepting.** The system will be quieter than it could be. Given that the alternative failure mode is being muted permanently, quiet is the right side to err on.

## Alternatives considered

**Let the LLM decide what is worth notifying** — rejected under §56.8. Model-judged importance is not stable across prompt or model changes, and it cannot be capped.

**Notify on every state change** — rejected outright by §27.

**User-configured frequency only** — rejected as the sole mechanism. §21 requires the settings, but defaults determine outcomes, and a product whose default is noisy gets muted before the user finds the setting.

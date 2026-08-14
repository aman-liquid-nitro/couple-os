# 0009. Two capture surfaces, split by visibility scope

Date: 2026-08-11
Status: Accepted
Supersedes: SPEC.md §7 (Life Inbox) as a single conversational interface

## Context

SPEC.md §7 makes one chat inbox the primary interface for everything. Writing the tool contracts exposed three problems.

**A chat inbox is single-player.** Each partner has their own conversation, so Partner B never sees what Partner A typed. For a product whose premise is a *shared* operating system, the couple's common state ends up living only in a database neither of them looks at.

**Visibility becomes a guess.** `create_memory` had to infer whether "she mentioned she really likes that bag" was private or shared. There is no clean boundary — "she mentioned she wants to visit her parents" is the same sentence shape with the opposite answer. The failure is asymmetric and unrecoverable: a leaked surprise cannot be un-seen. This was the weakest point in the design.

**Coordination and planning are different activities.** Logging that the AC was serviced needs no conversation. Working out an anniversary surprise does — it wants suggestions, budget checks, back-and-forth.

The insight that resolves all three: **the single-player objection to chat applies only to shared data.** Private data is single-player by definition. So chat is wrong for shared and right for private, and the two scopes want genuinely different interfaces.

## Decision

Two capture surfaces, split by visibility scope. **The surface determines the scope. The model never infers it.**

```text
SHARED  →  shared.md, a dump file
           Both partners read and write. Append-only log, batch reconciliation,
           change report. Everything captured here is visibility = shared_couple.

PRIVATE →  a per-partner chat thread
           Conversational, immediate, with in-band clarification. Only its owner
           can see it. Everything captured here is visibility = private_user.
```

`visibility` joins `couple_id` and `owner_user_id` on the list of values a tool may never accept as an argument (ADR 0004). It is injected by the application from the surface the input arrived on. This is the primary reason for the decision.

### Shared: `shared.md`

An **event log, not a document.** Blocks are appended, processed, then frozen into an archive section. Corrections are new lines — "actually dinner was 2400 not 4200" — routing them through §45's supersede path rather than through an edit.

Processing is explicit and batched. A `Process` action reads unprocessed blocks, extracts intents, calls tools, and rewrites the file in place:

```markdown
## Inbox
- got the AC serviced, 2500
- [receipt](attachments/ac-service.jpg)

## Needs your input
- "dinner was 2400" — who paid? Answer by adding a line below.

## Processed — 11 Aug
- ~~we're out of detergent~~ → shopping: detergent
- ~~spent 2400 on dinner~~ → expense ₹2,400 · Dining
- ~~i don't like italian anymore~~ → preference updated (superseded "Likes Italian food")
```

The **change report** after each run is the primary feedback mechanism, stating what was created, updated, superseded, and what failed — satisfying SPEC.md §46 and §32 more legibly than prose.

Blocks are content-hashed, with `(dump_file_id, content_hash)` unique, so re-processing cannot duplicate. Clarifications park in *Needs your input* rather than blocking capture — either partner can answer.

A single-line **quick-add** box appends to Inbox with no response and no state, keeping SPEC.md §24's standing-in-the-bathroom capture as cheap as it was in chat.

### Private: chat

Conversational, synchronous, one thread per partner. This is where surprises, gifts, personal goals and private reflections live (SPEC.md §19, §3.3), and where the assistant is most useful as a thinking partner rather than a filing clerk — suggesting date-night options, checking whether a gift fits the budget, working a plan out loud.

Every record it creates is `private_user`, owned by the speaker. There is no way to write a shared record from a private thread except by calling `share_memory`, which is `confirm`-tier (ADR 0008) and warns that sharing is effectively irreversible.

> **`share_memory` was never built, and V0 shipped without it.** TOOLS.md tiers it V1; this paragraph and V0_SCOPE read as though it exists. The decision above is unaffected — the direction that must stay closed is closed, and it is closed harder than described, because the door has no handle rather than a confirmed one. What the couple loses is the way *out*: something worked out privately can only reach shared state by being retyped into `shared.md`. Tracked as STATUS debt 49.

Clarifying questions happen in-band here, as SPEC.md §3.2 intended, because there is a person present and no coordination cost to interrupting them.

### Cross-cutting

**Misfiled content is flagged, never moved.** If gift- or surprise-shaped text appears in `shared.md`, the run reports it — "this looks like a surprise and it is in the shared file" — and takes no action. The system never silently relocates a user's words between privacy scopes. The user's choice of surface is authoritative; the flag is advisory.

**One pipeline.** Both surfaces produce blocks that run through the same validate → authorize → execute → audit path (ADR 0004). They differ in how input arrives and how results are reported, not in what happens to state.

**Act, then report.** Records are created immediately; the report is the review step. Everything is soft-deleted with a 30-day recovery window. `authorize`-tier operations still park for explicit confirmation.

**Attachments are stored and linked, not parsed.** An attachment referenced near a block is associated with whatever records that block produces. OCR remains SPEC.md §51 territory; `attachments.ocr_status` exists so the pipeline arrives without a migration.

## Consequences

**Easier.** The hardest privacy judgment in the product disappears — it is now a structural property, verified by row-level security rather than by prompt quality. Both partners see one shared surface. Each activity gets the interface that suits it: coordination gets a reviewable log, planning gets a conversation. Batch processing means the `deep` model runs once per dump rather than once per sentence, materially improving the ADR 0003 cost profile.

**Harder.** Two interfaces to build and maintain instead of one, which is real cost against SPEC.md §56.1 — mitigated by both feeding one pipeline, so the duplication is presentation-layer only. Users must learn that *where* they write determines *who sees it*; this is far more teachable than an invisible AI judgment, but it is not free. Shared feedback is no longer immediate, so the change report has to be excellent. Files grow without bound and need archive rollover. Two people editing `shared.md` need last-write-wins with a stale-version warning.

**Accepting.** A user who wants to capture something private while looking at the shared file has to switch surfaces. That friction is the mechanism, not a flaw in it.

**Risk to the V0 experiment.** If shared capture friction on mobile is too high, users stop capturing and V0 fails for an interface reason rather than an idea reason. Quick-add exists specifically to remove that confound and is not optional.

## Alternatives considered

**One chat inbox for everything, per SPEC.md §7** — rejected. Single-player for shared data, and it forces the visibility guess this decision eliminates.

**One dump file for everything, AI-infers visibility** — rejected. Keeps the guessing problem entirely, and makes every private thought a leak waiting on a misclassification.

**Dump files for both scopes (a private notebook per partner)** — rejected as the primary private surface, though `dump_files.kind = 'private'` remains in the schema, unused. Private capture is disproportionately planning work, which wants dialogue. A private notebook can be added later without migration if it turns out people want one.

**Chat for both scopes, with a shared thread** — rejected. A shared chat thread is a group chat: it accumulates conversation rather than state, and reviewing what the system understood means scrolling a transcript instead of reading a list.

**Real `.md` files synced from disk (Obsidian, Dropbox, iCloud)** — rejected for V0, attractive later. Files outliving the app fits SPEC.md §40's privacy stance, but it introduces sync conflicts, a file watcher and a merge story before the core idea is validated. Export to `.md` is cheap to add.

**Document-as-source-of-truth (the file *is* the state)** — rejected. Deleting a line would have to delete an expense, and reconciliation could not distinguish "removed on purpose" from "never seen". The append-only log avoids the class of problem.

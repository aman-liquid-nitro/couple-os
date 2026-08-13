# Status

**Updated:** 2026-08-13 · **Milestone:** M3 started. M0, M1 and M2 all met

This file records **state**. [IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md)
records **intent** — what each milestone is for and how it ends. Read the plan
to know where the project is going; read this to know where it actually is.

Keep it current at the end of a working session, not during. A status file
updated speculatively is worse than none.

---

## At a glance

| | |
|---|---|
| Milestone | M3 (the seven tools) started; M0, M1, M2 closed |
| Commits | 38 |
| Architecture decisions | 13 |
| Tests | 253 plus 42 SQL assertions, all shown capable of failing (7 need a model provider configured and fail without one) |
| Registered tools | 1 of the 7 writing tools (`create_shopping_item`), plus `request_clarification` |
| Mapped tables | 13 of 28 (+ `users`, `couples`, `couple_members`, `auth_tokens`, `sessions`) |
| Eval cases running | 4 of 55 |

Two real people can sign in with no password anywhere in the system, form a
couple, and each write to it as themselves. `shared.md` is now a file they both
edit rather than a box that forgets: it opens holding what the couple has
written, saves under an optimistic version so neither partner can silently erase
the other, and Process turns it into blocks — one model call per block, one
status per block, one transaction per block.

The change report is built from the input rather than from the actions, which
was the point of the milestone: a line no tool covers now appears in the report
saying so, where it used to disappear without trace.

Process now also **rewrites the file**. Settled lines move into a dated
`## Processed` section struck through with what they became, failed ones stay in
the inbox, and the inbox is what is left to do. That makes the file itself the
durable record — before it, closing the tab left the change report as a JSON
column nothing renders.

Quick-add is in: one line, one tap, no version to carry and nothing to read
back. ADR 0009 calls it not optional because a thought captured in a doorway is
what V0 is testing, and it must not cost more than that.

A run can now also **ask**. `request_clarification` is the third option the
model never had: rule 1 forbids inventing a missing value and ADR 0004 forbids
writing without a tool, so a note with a required value missing had no compliant
action and the line disappeared. It parks instead — `needs_input`, a question in
the file under *Needs your input*, and the rest of the run unaffected.

The private thread is in, and building it found a leak that predated it. The
row-level security policy on `conversation_messages` keyed privacy off who
authored a message, and the assistant's turns have no author — so a reply that
restates what one partner just said was readable by the other. Proved against the
running database before a line of the surface existed, and now scoped by session
instead: nine assertions in section H of `data/rls-tests.sql`, plus the same
property through EF Core, both shown to fail when the old policy is put back.

**M2 is met. Both surfaces produce units of input, one tool path underneath.**

---

## M0 · Walking skeleton

| Exit criterion | State |
|---|---|
| `docker compose up` reaches a healthy stack, three containers | done — verified from an empty volume; healthy `api` means `/health` reached Postgres |
| EF model agrees with `data/schema.sql`; parity test passes | done — 3 assertions (ADR 0012: SQL owns the schema, no migrations) |
| App connects as a role that is neither superuser nor `BYPASSRLS` | done — and the test suite refuses to run if it is |
| Partner B cannot read Partner A's private memory through `DbContext` | done |
| A request without session context returns zero rows | done — asserted, not incidental |
| Pooling test, interleaved requests, zero cross-contamination | done — 50 in C#, 1600 in SQL |
| One capture creates one shopping item and one `ai_actions` row | done — verified in a browser |
| Eval harness exists and runs, 3 cases wired in | done — 4 run; `EvalCoverage` reports the 28 blocked |

**Remaining:** nothing. The assumption M0 existed to falsify — that row-level
security might not survive EF Core's connection pooling — did not falsify, and
the walking skeleton walks.

---

## M1 · Identity

| Exit criterion | State |
|---|---|
| Two real people in two browsers, each attributed to themselves | done — verified in the running container: two sessions, two `ai_actions` rows naming different users, one couple |
| Correct isolation | done — asserted through the scope a real session produces, not through fixtures |
| Replay of a consumed token fails identically to an expired one | done — and to one that never existed; the result type has no field to tell them apart |
| Single use is atomic | done — 12 simultaneous uses of one link, exactly one wins |
| Rate limits hold and recover | done — asserted at the boundary and after the window passes |
| An unknown address is answered identically to a known one | done — same outcome, same subject, bodies differing only in the token |
| The seeded-couple path is gone | done — deleted, not disabled |

**No passwords exist anywhere in the codebase**, which is the ADR 0007 property
worth restating: there is no hashing to get wrong, no reset flow, and no breach
class involving leaked password hashes.

---

## M2 · Capture surfaces

| Exit criterion | State |
|---|---|
| Both surfaces produce blocks | done, with the wording read literally and rejected — see below |
| A second `Process` on an unchanged file creates nothing | done, and now twice over — the dedup index still holds, and the rewrite means the second run has nothing left to read |
| A correction line supersedes rather than duplicates | not started — needs `supersede`, which arrives with M3's tools |
| **The report accounts for every block, including those that produced no tool call** | done — the report is built from blocks, and every block carries a status |
| **`Process` rewrites the file in place** | done — settled lines archived, failed ones left in the inbox |
| **Quick-add** | done — one line, appended into the inbox, no version to carry and nothing to read back |
| **`Needs your input` parks clarifications without blocking other blocks** | done — verified in the browser: one line parked with its question, the other blocks in the run untouched, a second Process re-asked nothing |

**On "both surfaces produce blocks".** The private thread does not produce
`dump_blocks`, and forcing it to would have been a bug. `dump_blocks_dedup` is
`UNIQUE (dump_file_id, content_hash)` with one private file per member, so the
second "ok" in a thread would collide with the first and vanish — repetition is
noise in a dump file and meaning in a conversation. What ADR 0009 actually
requires is *"both surfaces produce blocks that run through the same
validate → authorize → execute → audit path"*, and that path is the tool
dispatcher, which both surfaces share. "Blocks" there is loose wording for "units
of input"; `dump_blocks` is the shared file's implementation of one and
`conversation_messages` is the thread's. `DumpFileKind.Private` already recorded
the same conclusion from the other end — it is unused because ADR 0009 chose chat.

**Also done, and not on the list:** the file model and its editor, `content_version`
optimistic concurrency with a stale-version warning, one transaction per block,
the run's counters and its report persisted to `dump_runs.report`.

**Remaining:** nothing. Both surfaces capture, the report accounts for the input,
a second run creates nothing, and a question can be asked on either surface — in
the file on one, in the reply on the other. `supersede` is the one exit criterion
that moved rather than closed: it needs a tool that arrives with M3.

---

## M3 · The seven tools

Started. Nothing on the exit list is met yet — the first slice is the machinery
five of the six remaining tools cannot be written without.

| Exit criterion | State |
|---|---|
| All seven tools work from both surfaces | 1 of 7 (`create_shopping_item`) |
| A forced tool failure never produces success language | asserted on both surfaces already, for the tools that exist |

**Done, and none of it a tool.** `DateExpressionResolver` and `ICoupleClock`
(debt 22, paid). Five of the six remaining tools take a date expression, and
`events.starts_at` is `NOT NULL` with no default, so the resolver is a
prerequisite rather than a detail.

**What the survey changed about the plan.** There is **no `reminders` table** —
`create_reminder` writes `tasks` with `kind = 'reminder'`, and
`tasks_reminder_needs_due` requires `due_at` when it does, so the distinction is
one column and one constraint rather than two tables. Three entities
(`tasks`, `expenses`, `events`) and **seven** enums (`memory_type`,
`memory_assertion`, `memory_status`, `data_source`, `task_kind`, `task_status`,
`priority_level`) do not exist in C# at all. A Domain entity named `Task` would
shadow `System.Threading.Tasks.Task` in every async file that touches it, so it
will not be called that.

---

## What exists

### Database
- `data/schema.sql` is authoritative (ADR 0012). 28 tables, **22** with `FORCE ROW LEVEL SECURITY`; the six exceptions are `users`, `couples`, `couple_members`, `auth_tokens`, `sessions` and `expense_categories`. `RlsCoverageTests` asserts that list in both directions — this file previously said 16, and the schema comment named three of the six.
- `data/rls-tests.sql` — 42 assertions: read isolation, write path, connection reuse, prepared statements under `force_generic_plan`, privilege escalation, and section H's private conversation.
- **`conversation_messages` is scoped by session, not by authorship**, and was not. The policy read `... OR user_id = app_current_user() OR user_id IS NULL`, with a comment claiming the last branch covered assistant turns "in the caller's session" — it never joined `conversation_sessions`, so it covered assistant turns in every session in the couple. An assistant turn is authorless by nature and restates what the person just said, so the partner could read a paraphrase of the surprise. Reproduced as `app_user` against the running database *before* the chat surface existed, then closed with an `EXISTS` against the session. The `visibility = 'shared_couple'` branch went with it: a message in one partner's thread is private whatever that column says, and the column's job is to scope the *records the turn produces* (ADR 0009), not the transcript.
- `conversation_sessions_one_open_per_member` — new, partial on `ended_at`. One thread per partner, so one person with two tabs cannot end up with two histories; a single-user race, which is the kind that reaches production because nobody thinks to test it.
- `data/rls-concurrency.sh` — 1600 interleaved transactions across 16 reused connections, `-M prepared`.
- Both verified to fail when a policy is removed.

### Application
- `IScopedUnitOfWork` / `ICoupleTransaction` — every read and write passes through a transaction scoped with `set_config(..., true)`.
- `ICoupleScopeAccessor` / `ICoupleScopeSetter` — reading and establishing the security context are separately grantable.
- Tool pipeline — `ITool`, `IToolRegistry`, `IToolDispatcher`, `AuditingToolDispatcher`. Forbidden arguments and undeclared properties are refused by the dispatcher, not by each tool.
- `RequestClarificationTool` — TOOLS.md 2a, and the only tool that writes no row. It returns `ToolExecution.Asks`, and `BlockProcessor` is what turns that into `status = 'needs_input'` and a filled `question`. Deliberately a *capability in the result* rather than a name the pipeline compares against: a tool is handed a couple, a user and a visibility and not the block it came from, so nothing in the tool layer can reach across and rewrite the pipeline's bookkeeping. Its two arguments compose ADR 0009's `"fragment" — question` here, because the model is the only thing that knows which half of a three-item note is in doubt; `FileRewriter` stopped quoting the block a second time.
- An open question **outranks** a success in the same block. A line that added detergent and asked who paid for dinner is not finished, and `Processed` is a status the file archives — filing it would take the question out of the inbox, which is the one place either partner would have seen it. The row that was created stays created (debt 31 is the other end of that trade).
- Asking is a `success` with a null `entity_type` and `entity_id`, so it is audited like everything else and counted like nothing: `CaptureReport.Applied` excludes it, and `dump_runs.entities_created` is that sequence's length. Both columns were already nullable and nothing had ever exercised it.
- `ICaptureProcessor` — one press of Process: intake, then every pending block, then the run's counters and report. Orchestrates only; it never calls a model.
- `IBlockProcessor` — one block from text to rows. The model call happens outside any transaction; the dispatches, the block's status and its entity links happen inside one; a block that throws is marked failed in a transaction of its own, because the failure being recorded is usually the failure that rolled the previous one back.
- `FileRewriter` — the writer half of `BlockSegmenter`, and its mirror: pure, static, and sharing one classifier (`BlockSegmenter.RoleOf`) rather than reimplementing "is this section ours". Settled blocks move into a dated `## Processed` section, parked ones into `## Needs your input`, and **failed ones stay in the inbox** because the inbox is what the file says is outstanding. Everything else — the couple's own headings, their blank lines — is left verbatim; the only reordering is lifting the inbox back above the archive, because an inbox below three months of history is an inbox nobody writes in from a phone. Matched by hash, never by the line numbers recorded when the block was first seen, because the file has been edited since.
- The rewrite's read and write share one transaction and the write carries the version the read returned, so a partner saving mid-run is refused exactly as they would be in the editor — `PartnerSavedFirst`, reported on the page, retried by the next Process. Rewriting from a stale copy is how a run would silently delete a line somebody had just typed.
- `QuickAdd` — one line into the inbox, spliced rather than re-rendered, so an append cannot reflow a file somebody is mid-sentence in. Appending at the *end* of the file is the obvious implementation and is wrong: after one run the end of the file is inside the archive, which the segmenter refuses to read, so every quick-added line would be stored, displayed, and never processed. The entry is written as a list item so that a line quick-added and the same words typed into the editor hash to one block rather than two.
- `ISharedFileEditor` — read and save `shared.md`. The version check is in the `UPDATE`'s `WHERE` clause, so two partners saving the same version produce exactly one winner. A refusal is a result, not an exception: the loser sees the partner's text and may save again on top of it deliberately (ADR 0009's last-write-wins-with-a-warning, both halves).
- `IPrivateThread` / `PrivateThread` — ADR 0009's private half, and the mirror of `CaptureProcessor` rather than a variant of it. What the two share is one provider, one registry, one dispatcher and therefore one tool path; what differs is arrival and reporting. The person's turn commits **before** the model is called, so a provider that is down costs a reply and not a message — and an unreachable model produces an assistant turn saying so, in its own transaction, for the same reason `BlockProcessor.FailAsync` needs one.
- Clarification is in-band here, and reuses the shared surface's tool rather than sitting beside it: a `request_clarification` call becomes the reply, outranking the model's own prose. Verified in the browser that the live model instead asks in prose and calls nothing, which `ChatPrompt` rule 3 asks for — and crucially does not invent an item name.
- The reply and the record are two fields, never folded together. A model can write "added that to your list" without calling the tool, and the only way a screen can contradict it is by holding both. The fallback for a silent model says how it went — `Recorded`, `Some of that went through`, `None of that went through` — and deliberately does not restate the change list: the first version did, and the browser showed one coffee twice.
- `DateExpressionResolver` — verbatim expressions to instants, in the couple's timezone. Pure over (expression, now, zone), so its tests fix "now" and do not rot as the calendar moves — which is the mistake debt 21 records in the eval set. It **refuses rather than guesses**, because `events.starts_at` is `NOT NULL` and the tempting way to satisfy that is to invent something; and it **never completes an expression silently**, so a filled-in year, hour or weekday comes back as a stated assumption. A year-less date resolves to the next occurrence, so a birthday said in December means next September rather than eight months into the past. A local time inside a daylight-saving gap is moved to the first instant that exists and says so — unreachable in Asia/Kolkata, and the first couple to set another zone should not be the test.
- `ICoupleClock` — reads `couples.timezone` once per request, on `IdentityDbContext` because `couples` has no row-level security and the read therefore needs no couple scope. The zone is cached and the instant is not, so a long run does not resolve its last block against the time its first one started. An unresolvable zone falls back to the schema's default and reports that it did, rather than failing somebody's reminder.
- `CapturePrompt` — versioned (`2026-08-13.1`), because the eval set judges this exact text.
- `ChatPrompt` — versioned (`2026-08-13.1`) and separate, because the two surfaces disagree about the rule that matters most in the other. CapturePrompt rule 1 forbids prose outright; here prose is the entire medium. No eval case judges it yet, so its version is a promise rather than a measurement. Rule 2 used to end "say nothing about it rather than guessing", which is what a model with no way to ask has to be told; it now names `request_clarification`, and rule 3 says outright that a date the person wrote is never a missing value.

### Identity (M1)
- `IdentityDbContext` — the five tables sign-in touches, and no couple-scoped table at all. A second context rather than more DbSets, because authentication runs *before* a couple scope exists and so cannot use `IScopedUnitOfWork`; given that, making it its own context buys an invariant the type system enforces. This is debt 8's seam built the other way round.
- `SecretToken` — the one place a bearer secret is created or hashed. 32 CSPRNG bytes, base64url, SHA-256 at rest, for both magic links and session cookies.
- `MagicLinkService` — issue and consume. Registration and sign-in are one path, not two, which is what makes enumeration resistance structural: an unknown address does the same work and sends the same mail, so there is no timing difference to measure.
- Single use is one `UPDATE ... WHERE consumed_at IS NULL AND expires_at > now()`. A read-then-write passes the replay test and fails the concurrency one.
- `SessionAuthenticationMiddleware` — resolves the cookie, sets the couple scope, and redirects everyone else. Nothing reaches a page without a scope.
- Rolling sessions renew in steps rather than on every request, so the common authenticated request is read-only. `last_seen_at` is therefore accurate to within an hour, which is fine for what it is and would not be if it were used for security decisions.
- Mail to maildev; every message also written to the log in Development, and delivery failure there does not fail sign-in — ADR 0007 requires the project to run locally with no mail provider.

### AI
- `ILlmProvider` in Application; `OllamaLlmProvider` in `CoupleOS.AI`. Application never references the AI project.
- One provider serves a local Ollama and Ollama's hosted service — identical `/api/chat`, so the difference is a base address and a bearer token ([ADR 0013](../decisions/0013-ollama-hosted-service-for-inference.md)). The credential's presence selects the host; `Name` reports `ollama-cloud` when set.
- Hosted default `gemma4:31b`, local default `qwen3.5:4b`, thinking disabled, `num_ctx` explicit. Selection and measurements in [ADR 0011](../decisions/0011-local-model-provider-for-development.md) and ADR 0013.
- The hosted free tier entitles **7 of the 18 models `/api/tags` lists** — the catalogue is not the entitlement. Of those 7, `gpt-oss:120b` and `:20b` fail the tool-call gate by returning one call and dropping the rest, which under ADR 0004 writes nothing and still renders a complete-looking report. `gemma4:31b` passes in ~2s.

### Web
- Razor Pages + htmx, no Bootstrap or jQuery (ADR 0010). htmx vendored locally, not from a CDN.
- One page: the `shared.md` editor, Save, Process, and a change report that lists every block under what became of it.
- The report's buckets used to be the `else` of its stranded-lines warning, so one line that failed on an earlier run hid everything the current run had just done — including a question waiting to be answered. Found while verifying the parking in the browser, on a file that happened to have both. The two facts are independent and are now printed as two.
- The file's version rides in a hidden field and is swapped back out of band by every response that writes — one partial owns that rule, because a response that changes the row and leaves the input alone makes the user's next press conflict with their own last one.
- A second page: the private thread. One input, no Process button and no version, and both absences are the design — a conversation has nobody else editing it, so there is no stale copy to detect, and it acts as you speak, so there is nothing to defer. The turn list is appended to with `hx-swap="beforeend"`, so a long thread is not re-rendered and re-posted on every message the way the shared textarea must be (debt 27).
- Sign in, check-your-email, callback, sign out, create couple, invite partner. Six screens, one input each.
- `/health` — plain text, unauthenticated, backed by `DatabaseHealthCheck`. Runs `SELECT 1`, so a healthy answer means the app reached Postgres as the non-superuser role. `CanConnectAsync` was the obvious implementation and the wrong one: it swallows the provider exception and returns a bare false, discarding the only part worth reading.

### Container
- `src/CoupleOS.Api/Dockerfile` — SDK build stage, `aspnet:10.0` runtime, non-root (uid 1654), 368 MB. Restore is a separate layer from the sources.
- `.dockerignore` keeps host `bin/`, `obj/`, `.env` and `appsettings.Development.json` out of the context, so the image built locally is the image built from a clean clone, and no secret reaches a layer.
- `api` is in the default compose stack. The `app` profile was waiting for the project to exist; it does.
- The runtime image ships no HTTP client at all — no curl, no wget, no netcat — so `curl` is installed for `HEALTHCHECK`. Without it `docker compose ps` could only ever say *running*, never *healthy*.
- The probe matches the response body rather than trusting curl's exit code, because `--fail` treats a 3xx as success and `UseHttpsRedirection` answers 307 if an HTTPS port is ever configured.

---

## What is not built

| | Milestone |
|---|---|
| A profile screen — `display_name` is the email's local part and cannot be changed | M2 or later |
| A session list, so "sign out everywhere" can be aimed rather than all-or-nothing | later |
| Answering a parked question, as a loop the system closes rather than the user retyping the line (debt 31) | M3 |
| Per-intent accounting on the private surface — the unit there is the whole message (debt 32) | M3 |
| Six of seven tools — task, reminder, expense, event, memory, search | M3 |
| Eval gates enforced as a build gate | M4 |
| Attachments and the read surface | M5 |

---

## Debts and deferred decisions

Each of these is deliberate and recorded where the code lives. Listed here so
they are visible in one place rather than discoverable only by reading commits.

**Numbers are never reused and never renumbered.** Code comments cite them —
`STATUS debt 8`, `debt 22`, `debt 24` — so a new entry takes the next free number
whichever section it belongs in, and a renumbering would silently repoint every
citation at the wrong paragraph.

**Blocking a specific future step**

1. **`action_outcome` has no `confirmation_required` label.** `AiActionAuditSink` throws rather than substituting a near-enough value. A migration is owed before the first `Confirm`-tier tool ships. *(ADR 0008, M3)*
2. **`memories` is mapped read-only.** ~~`type` and `content`~~ **`type`** is `NOT NULL` with no default and unmapped, so `create_memory` cannot be written until the entity carries it. `content` *is* mapped and has been — this entry overstated the gap and was corrected while surveying M3, which is the point at which a wrong debt description starts misdirecting work rather than merely being untidy. The real gap is wider than one column in a different direction: `assertion`, `status`, `subject_key`, `source`, `confidence`, `importance` and `expires_at` all have database defaults, so they do not block a write, but `create_memory`'s schema takes five of them as arguments and none can be set today. `SchemaParityTests` records the blocking half rather than tolerating it silently. *(M3)*
3. **`/health` is unauthenticated, and must stay that way.** The container probe has no credentials, so `SessionAuthenticationMiddleware` allow-lists it. It returns one word and never the exception text the logs carry; anything richer added there is readable by anyone who can reach the port.
4. ~~**Data protection keys are not persisted.**~~ **Paid.** A named volume holds the key ring, and the Dockerfile creates the directory owned by uid 1654 so the volume inherits that rather than being created root-owned. Verified the way it used to fail: a page rendered by one container still POSTs after `up -d --build`. It stopped being theoretical when it locked the sign-in form during M2's browser verification — the stale cookie is HttpOnly, so the only ways through were clearing cookies by hand or browsing from a different hostname.
5. **The invitation email does not name the inviter.** `invitedByDisplayName` is passed as null, so every invitation reads "Your partner has invited you". Wiring it needs the sender's display name, which is currently an email local part anyway — worth doing with the profile screen, not before. *(M2)*
31. **A parked question can be asked but not answered — not by the system, anyway.** The question reaches the file and the report; closing the loop does not exist, and it has two halves. The file says *"Answer by adding a line below"*, and that line becomes a block of its own with no memory of what it answers: the model is handed `amy paid` alone and has no expense to attach it to, and `dump_blocks.answered_by_block_id` is a column nothing writes. Alternatively the user edits the original line, which rehashes it into a *new* block — so every tool the first pass already ran runs again. That is harmless today by luck rather than design: `create_shopping_item` deduplicates on `normalized_name` and it is the only writing tool registered. The narrow fix is to carry the open questions into the prompt as context for the blocks that follow them, which is a prompt change and therefore an eval-set change (`CapturePrompt.Version`, and the cases in debt 21) — so it belongs with the milestone that registers the tools whose missing values are what gets asked about in the first place. *(M3)*

32. **The private surface accounts for tool calls, not for input — M2's own defect, back on the other surface.** Debt 6 was "the change report describes actions, not input", and blocks paid it: every line gets a status, so silence is unrepresentable. The private thread has no equivalent, because its unit of input is the whole message. Seen on the first real turn: *"i want to get ben a watch for his birthday, and we're out of coffee"* recorded the coffee and said nothing whatsoever about the watch — no tool covers it, and nothing structural forced the reply to admit that. It is milder here than it was there, because `ChatPrompt` rule 1 asks for prose and a well-behaved model does say what it could not do, but *asking* is not the same as making it unrepresentable, which is the standard the shared surface now meets. The fix is to segment a message into intents the way a file is segmented into blocks, and it wants the tools that make multi-intent messages common — six of seven arrive in M3. *(M3)*
33. **A private thread's history is capped at twenty turns and nothing says so.** `ChatPrompt.HistoryTurns` truncates silently: turn twenty-one is answered by a model that cannot see turn one, and the person gets no indication that the assistant has stopped being able to remember. This is debt 8 with a conversation attached — the tool catalogue and the history both grow, and they multiply rather than add. The honest fix is summarising older turns rather than dropping them, because a thread that forgets without saying so contradicts itself and looks like a bug in the model. Twenty is enough for the sessions V0 is meant to produce, so this becomes real the first time somebody has a long one. *(V1)*
34. **Nothing ends a thread, so `conversation_sessions.ended_at` is never written.** One open thread per member forever, growing without bound — the same shape as debt 27's archive, and cheaper today only because the history sent to a model is capped (debt 33) and the page renders the same window. A "start a new conversation" button is what writes the column, and `conversation_sessions_one_open_per_member` is partial on `ended_at` specifically so it can be added without a schema change. *(M5)*

**Design questions with a real answer needed later**

6. ~~**The change report describes actions, not input.**~~ **Paid.** The report is built from blocks: one status each, and a block that produced no tool call is rendered under "read, nothing to do" carrying the model's own words. Verified in the browser — the dinner line that started all this now reads *"create calendar event was not applied — No tool named 'create_calendar_event' is registered."*
7. ~~**One transaction per capture run.**~~ **Paid.** Intake is atomic; processing takes one transaction per block, and a block that throws is marked failed in a transaction of its own. A late failure in a long dump no longer discards the successes before it, and a test asserts exactly that.
8. **Prompt cost scales with the tool catalogue.** Measured 660 prompt tokens for 3 tools; 17 tools projects to ~3060 per block, and ~62s for a 20-block dump. Two levers recorded in ADR 0011: filter tools per block, or batch blocks per call. *(M3)*
9. **`IScopedUnitOfWork` is a seam by convention, not construction.** Nothing stops a future caller injecting `CoupleOsDbContext` directly. Row-level security makes that fail closed rather than leak, so the consequence is an empty list rather than a breach — but the type system does not enforce it. `DatabaseHealthCheck` is now the only deliberate exception, and documents why it reads no rows. Identity is not a second one: it has its own context with no couple-scoped table on it, which is this same seam built by construction — the version worth copying if this debt is ever paid.
29. **Nothing decays, and nothing goes stale.** A preference recorded in March and one recorded yesterday are both simply live. `search_memory`'s ranking blends recency, so an old memory sorts lower — but sorting is not expiring, and nothing distinguishes "still true, just old" from "was true once". Three shapes of one gap, wanting one answer rather than three: a `temporary_context` memory has no expiry (SPEC.md §8 names the type; nothing ages it out), an `inferred` memory is never re-asked even though ADR 0006 says confirming one is how it becomes fact, and a preference contradicted only *implicitly* — by later behaviour rather than by a sentence — never supersedes, because `supersede` fires on an explicit contradiction and silence is not one. This is ADR 0006's own corrosive example wearing a different hat: not a guess promoted to fact, but a fact nobody noticed had lapsed. Needs a corpus before it can be designed against, which is the same reason contradiction handling (SPEC.md §45) is cut from V0 — so the honest sequence is a week of real use first. *(V1)*
30. **The shared surface can only be written to, never asked.** `shared.md` captures; the private thread converses. So the only way to ask what the couple knows is to ask in a private thread — correct and intended (see below), and still an odd shape to explain to a user: *ask your private assistant what we both know*. A `Process` run answers no questions, and "what do we know about Priya's parents" is a block the model reads and ignores. M5's read surface answers the browsing half of this; nothing in the plan gives the shared surface a query path, and two people looking at one file together is exactly where one would be asked for. *(M5, or a deliberate no)*

**Smaller**

10. ~~**`CaptureProcessor` has no unit tests.**~~ **Paid.** `BlockProcessorStatusTests` and `BlockProcessorAttributionTests` run the pipeline against fakes — no database, no GPU, 55 unit tests in 80ms. The status rules are decided in C# and are asserted there rather than through a model that might disagree with itself twice in a row.
11. **Three AI provider tests each make a separate model call** for what is one call's worth of assertions — about 6s of GPU per run, and three chances for a non-deterministic model to disagree with itself.
12. **`LlmUsage.Duration` includes HTTP and deserialisation**, so it reads slightly high. Fine for cost auditing, misleading as a benchmark.
13. **`Temperature = 0` is hard-coded** in `OllamaLlmProvider` rather than an option. Right for extraction; the wrong place for the decision to live if the chat surface ever wants warmth.
14. **Base images float on the `10.0` tag, not a digest.** The build is reproducible in the sense that matters today — restore is pinned by `Directory.Packages.props` — but two builds a month apart can sit on different SDK patches. Pin when there is somewhere to deploy to. *(M5)*
15. **Rate limits are counted from `auth_tokens` rows.** Nothing deletes them today, so the count is sound — but a future cleanup job that prunes consumed tokens would silently widen every window it touched.
16. **`SmtpEmailSender` cannot be cancelled mid-send.** `SmtpClient` has no cancellable send, so the token is observed before the call and not during it. Stated in the code rather than hidden behind a parameter that does nothing.
17. **`PublicBaseUrl` is unset, so link URLs come from the request's `Host` header.** Correct for localhost and containers, and attacker-controlled in general: a forged Host would mint links pointing elsewhere. Set it before this is reachable from a network you do not control. *(M5)*
18. **The RLS harnesses leave two tables behind.** `probe` and `t_results` persist in whatever database they ran against, unprotected and granted to the app role. Harmless in development, and something to remove before either harness is ever pointed at a deployed database.
19. **`ai_actions.estimated_cost` is still unwritten**, deliberately: Ollama is free on both hosts, so any figure would be invented, and SPEC.md 50 wants one someone can act on. `provider`, `model`, `llm_role`, `prompt_tokens` and `completion_tokens` are now populated — token counts on one row per completion so `SUM` is the real figure rather than N times it. Map the cost column alongside the rate table that makes it meaningful.
20. **Three environment variables in `.env.example` set nothing — two fixed, and the class of defect is the point.** `OLLAMA_KEEP_ALIVE` was read by no code and passed to no container (Ollama reads it as a *server* variable, and Ollama is not in the compose stack) — now removed from `.env.example` rather than left implying it worked. `LLM_FAST_MODEL`/`LLM_DEEP_MODEL` reached the container as `Llm__Roles__*` while `OllamaOptions` binds `Llm:Ollama:*` — fixed in compose, but the pattern is the point: a documented variable that quietly does nothing outlasts the person who wrote it. Nothing asserts that a configuration key is read by anyone.
21. **The eval set still encodes the pre-fix date contract.** ~~Eight~~ **Six** cases in `data/eval-cases.jsonl` expect resolved timestamps — `happy-003`, `happy-005`, `happy-006`, `multi-001`, `multi-002` carry absolute dates like `"due_at": "2026-08-12"` and `"starts_at": "2026-12-14"`, and `amb-006` carries `"expires_at": "+30d"`, which is expression-shaped in a field named for the resolved column. Eight was wrong and counted the two clarification cases (`amb-001`, `unknowndate-002`) that name `due_at`/`starts_at` only in `clarification_about`, where the old field name is *correct* — the question genuinely is about the resolved column. Counted properly while surveying M3. **A grep for any `*_expression` key in the file returns zero**, so the set does not merely encode the old contract, it has no example of the new one. They are inert because those tools are unregistered and `EvalCoverage` reports them blocked, so M3 unblocks tests that assert the wrong thing. Two problems, not one: the contract is wrong *and* a hard-coded absolute date rots as "today" moves. Not rewritten here — expectations are a measurement decision, and they belong with the milestone that registers the tools and can watch them pass. *(M3)*
22. ~~**Date resolution has nowhere to live.**~~ **Paid.** `DateExpressionResolver` is a pure function over (expression, now, zone), and `ICoupleClock` reads `couples.timezone` once per request — the first line of C# to consult a column that had a default and no reader for three milestones, which is debt 20's shape. Two rules carry it: it refuses rather than guesses, because `events.starts_at` is `NOT NULL` and the tempting way to satisfy that is to invent something; and it never completes an expression silently, so filling in a year, an hour or which Friday comes back as a stated assumption. The grammar is a small closed set and the list it refuses — "sometime next month", "after the wedding", "soon" — is as tested as the list it accepts. The timezone landed on the tools rather than on `ToolExecutionContext` as this entry predicted: that record is "everything a tool is allowed to know about who is calling", and a clock is a service, not caller identity. **`users.timezone` is still unread** — per-person zones are a real feature and V0 has no screen that would set one.
23. **No CI.** Deliberately deferred. "CI gate" currently means a command someone remembers to run.
24. **A failed block is never retried, and now the report at least says so.** `PendingAsync` reads `unprocessed`, so a block that failed because Ollama was unreachable stays failed and pressing Process again does nothing for it. The rewrite made this visible rather than merely true: it files the successes out of the inbox, so what is left in front of a couple who are up to date is *precisely* the failures — and the report used to greet that with "all 1 block in the file were processed by an earlier run", which is success language over a failed action and exactly what SPEC.md 46 forbids. `CaptureReport.Stranded` now counts the failed blocks the file still contains, intersected against what the file currently says so a line the user deleted stops being warned about, and the report names them and says to edit and re-press. **The retry itself is still owed.** The narrow version is the right one: re-run `failed` alone, not `ignored`, which is why the blanket retry was rejected here in the first place. *(M3)*
25. **A run's cost is now per block, and it shows.** Eleven blocks took 26.9s against `gemma4:31b` at ~2.4s each, with the full tool catalogue in every prompt. This is debt 8 arriving as a measurement rather than a projection: the levers in ADR 0011 — filter tools per block, or batch blocks per call — are the same, and the second one trades away the per-block status this milestone was built for. *(M3)*
26. **Process saves unconditionally, so a run that does nothing still bumps the version.** The save has to come first — processing text the file does not contain would report on something nobody could go back and read — but it writes even when the text is byte-for-byte identical, and `SaveSharedAsync` increments on every accepted write. Two presses that changed nothing took the file 5 → 6 → 7 during verification. Harmless to the person pressing it and not to the other one: their open editor goes stale, and their next Save warns them about a partner who wrote nothing. The fix is a no-op check in the `UPDATE`'s `WHERE` clause, not in C#, for the same reason the version check lives there.
27. **The archive grows without bound.** ADR 0009 names rollover as a consequence and nothing implements it: every run appends a dated section and none are ever pruned or rolled into a separate file. It costs nothing today — the segmenter refuses to read those sections, so the archive never reaches a prompt — but it is loaded, rendered into a textarea, and posted back on every Save, so the cost lands on the phone rather than on the model. *(M5)*
28. **Two test classes share a serialized collection because they share one row.** `shared.md` is one row per couple, so `SharedFileEditorTests` and `CaptureIntakeTests` cannot run in parallel against the fixture couple. Correct and cheap today; the honest fix is a couple per test class, and it is not worth building until the suite is slow enough to care.

---

## Things proven, and worth not re-litigating

- Row-level security holds under connection pooling **and** prepared statements, including `force_generic_plan`. *(ADR 0005 verification section)*
- `SET LOCAL` cannot take a query parameter; `set_config(name, value, true)` is exactly equivalent and parameterises. Session-level `SET` leaks across pooled transactions — reproduced in three statements.
- A superuser connection bypasses every policy. The app role is `NOSUPERUSER NOBYPASSRLS`, and the test suite refuses to run as anything else.
- `qwen3.5:4b` fits entirely in 8 GB of VRAM and answers a block in ~1.9s warm; `qwen3.5:9b` spills 12% to CPU for identical output.
- Disabling thinking removed 82% of generated tokens and made the same call 4.6x faster.
- `pg_isready` over the unix socket reports ready **while the init scripts are still running** — the entrypoint's temporary server has `listen_addresses=''`, so it is unreachable over TCP but not over the socket. The db healthcheck requires `-h 127.0.0.1` for that reason; without it `api` can start against a database whose 28 tables do not exist yet.
- `mcr.microsoft.com/dotnet/aspnet:10.0` contains no HTTP client. Verified: no curl, no wget, no netcat. Only `openssl`, which cannot speak plain HTTP.
- `DbContext.Database.CanConnectAsync()` returns false rather than throwing, discarding the reason. Anything that needs to report *why* the database is unreachable has to issue the statement itself.
- **EF Core orders inserts by the relationships in the model, not by the database's foreign keys.** Two entities inserted in one `SaveChanges` with no declared relationship between them get an arbitrary order — here, `couple_members` before `couples`, failing on the FK every time. Declaring `HasOne<Couple>().WithMany().HasForeignKey(...)` fixes the ordering; no navigation property is needed, or wanted.
- **Shared knowledge is readable from a private thread, and that is the intended direction.** Scope is asymmetric on purpose: a search from a private thread sees that partner's private rows *plus* the shared ones, and a search from `shared.md` sees only shared ones. Confirmed as correct rather than tolerated — a private conversation that could not consult what the couple jointly knows would be useless as a thinking partner, and it leaks nothing, because the flow is private-reads-shared. The direction that must never open is the reverse, and `share_memory` is the only path across it: user-initiated, `confirm`-tier, warned as irreversible.
- **A policy that keys privacy off authorship gets the authorless rows wrong, and the authorless rows are the dangerous ones.** `conversation_messages` allowed `user_id IS NULL` so that the assistant's turns would be readable; nobody noticed that this made them readable by *everyone in the couple*, because the branch had a comment explaining what it was meant to do. The unit of privacy for a conversation is the conversation, not the sentence — and a comment asserting a join that is not in the SQL is worse than no comment. Found by reading the policy while planning a feature that would have shipped on top of it.
- **The right time to read a policy is before writing the code that depends on it.** This one had been in `data/schema.sql` since the first commit and passed every existing test, because nothing wrote a row it governed. A table with no rows has no leaks; the coverage test that counts which tables have RLS could not tell the difference.
- Making registration and sign-in one code path removes enumeration as a *category* rather than mitigating it. There is no "address not found" branch to have different timing, so nothing has to be padded or constant-timed.
- `Secure` cookies work over `http://localhost` — browsers treat localhost as a secure context — so the attribute needs no environment switch, and therefore cannot be misconfigured in one.
- `SameSite=Strict` would break magic links. The cookie is set on a response to a top-level navigation from a mail client; under Strict it is withheld on the redirect that follows, and a valid link lands back on the sign-in page.
- **Docker seeds a new named volume from the image directory it covers, ownership included — but only if that directory exists in the image.** Mount a volume over a path the image does not have and Docker creates it root-owned, so a container running as a non-root user cannot write to its own volume. The fix is a `mkdir` and a `chown` at build time, not a runtime workaround.
- **Optimistic concurrency is for replacing, and a lock is for appending.** Two partners saving the same file genuinely conflict and one has to be told; two partners adding a line do not, and a version check there can only refuse work that should have happened. Retrying the refusal was the first implementation and it starved — twelve concurrent appends leave the unlucky writers exhausting their attempts while the lucky ones keep winning, a livelock with a timeout bolted on. `SELECT … FOR UPDATE` turns the same contention into a queue and every line lands. The sequential test passed under both designs; only the contended one said which had been chosen. Same lesson as M1's magic links, opposite conclusion, because the operation is different.
- **A file the system writes is a file the system reads back, and the loop has to be closed by a test rather than by care.** The rewrite's output is the next run's input. A rewrite producing anything the segmenter reads as input would put the whole archive through a model call on every press of Process, and the dedup index would hide it perfectly — no duplicate rows, just a run that got slower every day. The assertion that matters is not "the output looks right" but `Segment(Rewrite(x))` containing only what is genuinely unfinished.
- Reader and writer of the same format need one classifier, not two agreeing ones. `BlockSegmenter.RoleOf` is public for that reason alone: a second implementation of "is this heading one of ours" is a second chance to disagree, and disagreement here is the failure above.
- A count derived by subtraction lies as soon as the two quantities stop measuring the same set. `AlreadyRecorded` was blocks-seen minus blocks-handled, which went negative the first time a run picked up a block the file no longer contained — and a negative count rendered as nothing at all, so the report looked correct. Found by reading the running page, not by a test.

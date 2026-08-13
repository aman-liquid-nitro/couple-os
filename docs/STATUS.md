# Status

**Updated:** 2026-08-13 · **Milestone:** M2 in progress. M0 and M1 both fully met

This file records **state**. [IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md)
records **intent** — what each milestone is for and how it ends. Read the plan
to know where the project is going; read this to know where it actually is.

Keep it current at the end of a working session, not during. A status file
updated speculatively is worse than none.

---

## At a glance

| | |
|---|---|
| Milestone | M2 (capture surfaces) in progress; M0 and M1 closed |
| Commits | 37 |
| Architecture decisions | 13 |
| Tests | 163, all shown capable of failing (7 need a local Ollama and fail without one) |
| Registered tools | 1 of 7 (`create_shopping_item`) |
| Mapped tables | 11 of 28 (+ `users`, `couples`, `couple_members`, `auth_tokens`, `sessions`) |
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

**M2's shared surface is most of the way there. The quick-add box,
`needs_input` parking and the private thread are not built.**

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
| Both surfaces produce blocks | half — the shared file does; the private thread does not exist |
| A second `Process` on an unchanged file creates nothing | done, and now twice over — the dedup index still holds, and the rewrite means the second run has nothing left to read |
| A correction line supersedes rather than duplicates | not started — needs `supersede`, which arrives with M3's tools |
| **The report accounts for every block, including those that produced no tool call** | done — the report is built from blocks, and every block carries a status |
| **`Process` rewrites the file in place** | done — settled lines archived, failed ones left in the inbox |

**Also done, and not on the list:** the file model and its editor, `content_version`
optimistic concurrency with a stale-version warning, one transaction per block,
the run's counters and its report persisted to `dump_runs.report`.

**Remaining:** the quick-add box, `request_clarification` and the `needs_input`
parking it fills (the writer for that section exists and nothing fills it yet),
and the private chat surface.

---

## What exists

### Database
- `data/schema.sql` is authoritative (ADR 0012). 28 tables, **22** with `FORCE ROW LEVEL SECURITY`; the six exceptions are `users`, `couples`, `couple_members`, `auth_tokens`, `sessions` and `expense_categories`. `RlsCoverageTests` asserts that list in both directions — this file previously said 16, and the schema comment named three of the six.
- `data/rls-tests.sql` — 33 assertions: read isolation, write path, connection reuse, prepared statements under `force_generic_plan`, privilege escalation.
- `data/rls-concurrency.sh` — 1600 interleaved transactions across 16 reused connections, `-M prepared`.
- Both verified to fail when a policy is removed.

### Application
- `IScopedUnitOfWork` / `ICoupleTransaction` — every read and write passes through a transaction scoped with `set_config(..., true)`.
- `ICoupleScopeAccessor` / `ICoupleScopeSetter` — reading and establishing the security context are separately grantable.
- Tool pipeline — `ITool`, `IToolRegistry`, `IToolDispatcher`, `AuditingToolDispatcher`. Forbidden arguments and undeclared properties are refused by the dispatcher, not by each tool.
- `ICaptureProcessor` — one press of Process: intake, then every pending block, then the run's counters and report. Orchestrates only; it never calls a model.
- `IBlockProcessor` — one block from text to rows. The model call happens outside any transaction; the dispatches, the block's status and its entity links happen inside one; a block that throws is marked failed in a transaction of its own, because the failure being recorded is usually the failure that rolled the previous one back.
- `FileRewriter` — the writer half of `BlockSegmenter`, and its mirror: pure, static, and sharing one classifier (`BlockSegmenter.RoleOf`) rather than reimplementing "is this section ours". Settled blocks move into a dated `## Processed` section, parked ones into `## Needs your input`, and **failed ones stay in the inbox** because the inbox is what the file says is outstanding. Everything else — the couple's own headings, their blank lines — is left verbatim; the only reordering is lifting the inbox back above the archive, because an inbox below three months of history is an inbox nobody writes in from a phone. Matched by hash, never by the line numbers recorded when the block was first seen, because the file has been edited since.
- The rewrite's read and write share one transaction and the write carries the version the read returned, so a partner saving mid-run is refused exactly as they would be in the editor — `PartnerSavedFirst`, reported on the page, retried by the next Process. Rewriting from a stale copy is how a run would silently delete a line somebody had just typed.
- `ISharedFileEditor` — read and save `shared.md`. The version check is in the `UPDATE`'s `WHERE` clause, so two partners saving the same version produce exactly one winner. A refusal is a result, not an exception: the loser sees the partner's text and may save again on top of it deliberately (ADR 0009's last-write-wins-with-a-warning, both halves).
- `CapturePrompt` — versioned (`2026-08-12.1`), because the eval set judges this exact text.

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
- The file's version rides in a hidden field and is swapped back out of band by every response that writes — one partial owns that rule, because a response that changes the row and leaves the input alone makes the user's next press conflict with their own last one.
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
| The single-line quick-add box (ADR 0009 calls it not optional) | M2 |
| The private chat surface | M2 |
| `needs_input` parking and `request_clarification` implementation | M2 |
| Six of seven tools — task, reminder, expense, event, memory, search | M3 |
| Eval gates enforced as a build gate | M4 |
| Attachments and the read surface | M5 |

---

## Debts and deferred decisions

Each of these is deliberate and recorded where the code lives. Listed here so
they are visible in one place rather than discoverable only by reading commits.

**Blocking a specific future step**

1. **`action_outcome` has no `confirmation_required` label.** `AiActionAuditSink` throws rather than substituting a near-enough value. A migration is owed before the first `Confirm`-tier tool ships. *(ADR 0008, M3)*
2. **`memories` is mapped read-only.** `type` and `content` are `NOT NULL` with no default and are unmapped, so `create_memory` cannot be written until the entity carries them. `SchemaParityTests` records this rather than tolerating it silently. *(M3)*
3. **`/health` is unauthenticated, and must stay that way.** The container probe has no credentials, so `SessionAuthenticationMiddleware` allow-lists it. It returns one word and never the exception text the logs carry; anything richer added there is readable by anyone who can reach the port.
4. ~~**Data protection keys are not persisted.**~~ **Paid.** A named volume holds the key ring, and the Dockerfile creates the directory owned by uid 1654 so the volume inherits that rather than being created root-owned. Verified the way it used to fail: a page rendered by one container still POSTs after `up -d --build`. It stopped being theoretical when it locked the sign-in form during M2's browser verification — the stale cookie is HttpOnly, so the only ways through were clearing cookies by hand or browsing from a different hostname.
5. **The invitation email does not name the inviter.** `invitedByDisplayName` is passed as null, so every invitation reads "Your partner has invited you". Wiring it needs the sender's display name, which is currently an email local part anyway — worth doing with the profile screen, not before. *(M2)*

**Design questions with a real answer needed later**

6. ~~**The change report describes actions, not input.**~~ **Paid.** The report is built from blocks: one status each, and a block that produced no tool call is rendered under "read, nothing to do" carrying the model's own words. Verified in the browser — the dinner line that started all this now reads *"create calendar event was not applied — No tool named 'create_calendar_event' is registered."*
7. ~~**One transaction per capture run.**~~ **Paid.** Intake is atomic; processing takes one transaction per block, and a block that throws is marked failed in a transaction of its own. A late failure in a long dump no longer discards the successes before it, and a test asserts exactly that.
8. **Prompt cost scales with the tool catalogue.** Measured 660 prompt tokens for 3 tools; 17 tools projects to ~3060 per block, and ~62s for a 20-block dump. Two levers recorded in ADR 0011: filter tools per block, or batch blocks per call. *(M3)*
9. **`IScopedUnitOfWork` is a seam by convention, not construction.** Nothing stops a future caller injecting `CoupleOsDbContext` directly. Row-level security makes that fail closed rather than leak, so the consequence is an empty list rather than a breach — but the type system does not enforce it. `DatabaseHealthCheck` is now the only deliberate exception, and documents why it reads no rows. Identity is not a second one: it has its own context with no couple-scoped table on it, which is this same seam built by construction — the version worth copying if this debt is ever paid.

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
21. **The eval set still encodes the pre-fix date contract.** Eight cases in `data/eval-cases.jsonl` expect resolved timestamps — `"due_at": "2026-08-12"`, `"starts_at": "2026-12-14"` — which is now the behaviour TOOLS.md forbids. They are inert because those tools are unregistered and `EvalCoverage` reports them blocked, so M3 unblocks tests that assert the wrong thing. Two problems, not one: the contract is wrong *and* a hard-coded absolute date rots as "today" moves. Not rewritten here — expectations are a measurement decision, and they belong with the milestone that registers the tools and can watch them pass. *(M3)*
22. **Date resolution has nowhere to live.** TOOLS.md now declares expression fields on all eight date arguments, and the resolver they imply does not exist. It needs the couple's timezone, which `ToolExecutionContext` does not carry — `couples.timezone` and `users.timezone` are in the schema and unread. Owed before the first date-bearing tool ships. *(M3)*
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
- Making registration and sign-in one code path removes enumeration as a *category* rather than mitigating it. There is no "address not found" branch to have different timing, so nothing has to be padded or constant-timed.
- `Secure` cookies work over `http://localhost` — browsers treat localhost as a secure context — so the attribute needs no environment switch, and therefore cannot be misconfigured in one.
- `SameSite=Strict` would break magic links. The cookie is set on a response to a top-level navigation from a mail client; under Strict it is withheld on the redirect that follows, and a valid link lands back on the sign-in page.
- **Docker seeds a new named volume from the image directory it covers, ownership included — but only if that directory exists in the image.** Mount a volume over a path the image does not have and Docker creates it root-owned, so a container running as a non-root user cannot write to its own volume. The fix is a `mkdir` and a `chown` at build time, not a runtime workaround.
- **A file the system writes is a file the system reads back, and the loop has to be closed by a test rather than by care.** The rewrite's output is the next run's input. A rewrite producing anything the segmenter reads as input would put the whole archive through a model call on every press of Process, and the dedup index would hide it perfectly — no duplicate rows, just a run that got slower every day. The assertion that matters is not "the output looks right" but `Segment(Rewrite(x))` containing only what is genuinely unfinished.
- Reader and writer of the same format need one classifier, not two agreeing ones. `BlockSegmenter.RoleOf` is public for that reason alone: a second implementation of "is this heading one of ours" is a second chance to disagree, and disagreement here is the failure above.
- A count derived by subtraction lies as soon as the two quantities stop measuring the same set. `AlreadyRecorded` was blocks-seen minus blocks-handled, which went negative the first time a run picked up a block the file no longer contained — and a negative count rendered as nothing at all, so the report looked correct. Found by reading the running page, not by a test.

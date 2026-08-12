# Status

**Updated:** 2026-08-12 · **Milestone:** M1 complete. M0 and M1 both fully met

This file records **state**. [IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md)
records **intent** — what each milestone is for and how it ends. Read the plan
to know where the project is going; read this to know where it actually is.

Keep it current at the end of a working session, not during. A status file
updated speculatively is worse than none.

---

## At a glance

| | |
|---|---|
| Milestone | M1 (identity) complete; M0 before it |
| Commits | 29 |
| Architecture decisions | 13 |
| Tests | 88, all shown capable of failing |
| Registered tools | 1 of 7 (`create_shopping_item`) |
| Mapped tables | 8 of 28 (+ `users`, `couples`, `couple_members`, `auth_tokens`, `sessions`) |
| Eval cases running | 4 of 55 |

Two real people can now sign in with no password anywhere in the system, form a
couple, and each write to it as themselves: text typed into a page is extracted
by a model — local, or Ollama's hosted service so a laptop GPU is not saturated
for the duration (ADR 0013) — validated and executed through the tool layer,
written under row-level security scoped to whoever's session sent it, audited,
and reported back in about three seconds.

**M1 is closed. The next branch is M2, capture surfaces.**

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
- `ICaptureProcessor` — joins model to tool layer. Orchestrates only.
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
- One page: `shared.md` editor, Process button, change report.
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
| Block segmentation, content hashing, re-`Process` doing nothing | M2 |
| The private chat surface | M2 |
| `dump_files` / `dump_blocks` / `dump_runs` persistence | M2 |
| Change report persisted to `dump_runs.report` | M2 |
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
4. **Data protection keys are not persisted.** The container writes them to `/home/app/.aspnet/DataProtection-Keys`, which dies with the container, so a page left open across `docker compose up --build` fails its next POST with a 400 antiforgery error. *Narrower than it first looked:* session cookies carry our own hashed token and are not data-protected, so a rebuild does **not** sign anyone out. Antiforgery only. *(M2)*
5. **The invitation email does not name the inviter.** `invitedByDisplayName` is passed as null, so every invitation reads "Your partner has invited you". Wiring it needs the sender's display name, which is currently an email local part anyway — worth doing with the profile screen, not before. *(M2)*

**Design questions with a real answer needed later**

6. **The change report describes actions, not input.** A line producing no tool call vanishes without trace — observed on the first real run, where an event silently disappeared. M2's exit criteria now require the report to account for every block. *(M2)*
7. **One transaction per capture run.** Correct for a five-line note, questionable for a two-hundred-line dump where a late failure discards earlier successes. *(M2)*
8. **Prompt cost scales with the tool catalogue.** Measured 660 prompt tokens for 3 tools; 17 tools projects to ~3060 per block, and ~62s for a 20-block dump. Two levers recorded in ADR 0011: filter tools per block, or batch blocks per call. *(M3)*
9. **`IScopedUnitOfWork` is a seam by convention, not construction.** Nothing stops a future caller injecting `CoupleOsDbContext` directly. Row-level security makes that fail closed rather than leak, so the consequence is an empty list rather than a breach — but the type system does not enforce it. `DatabaseHealthCheck` is now the only deliberate exception, and documents why it reads no rows. Identity is not a second one: it has its own context with no couple-scoped table on it, which is this same seam built by construction — the version worth copying if this debt is ever paid.

**Smaller**

10. **`CaptureProcessor` has no unit tests.** Its behaviour is only covered by the manual browser run and indirectly by the pipeline tests. It was built to be testable against a fake provider; that test has not been written.
11. **Three AI provider tests each make a separate model call** for what is one call's worth of assertions — about 6s of GPU per run, and three chances for a non-deterministic model to disagree with itself.
12. **`LlmUsage.Duration` includes HTTP and deserialisation**, so it reads slightly high. Fine for cost auditing, misleading as a benchmark.
13. **`Temperature = 0` is hard-coded** in `OllamaLlmProvider` rather than an option. Right for extraction; the wrong place for the decision to live if the chat surface ever wants warmth.
14. **Base images float on the `10.0` tag, not a digest.** The build is reproducible in the sense that matters today — restore is pinned by `Directory.Packages.props` — but two builds a month apart can sit on different SDK patches. Pin when there is somewhere to deploy to. *(M5)*
15. **Rate limits are counted from `auth_tokens` rows.** Nothing deletes them today, so the count is sound — but a future cleanup job that prunes consumed tokens would silently widen every window it touched.
16. **`SmtpEmailSender` cannot be cancelled mid-send.** `SmtpClient` has no cancellable send, so the token is observed before the call and not during it. Stated in the code rather than hidden behind a parameter that does nothing.
17. **`PublicBaseUrl` is unset, so link URLs come from the request's `Host` header.** Correct for localhost and containers, and attacker-controlled in general: a forged Host would mint links pointing elsewhere. Set it before this is reachable from a network you do not control. *(M5)*
18. **The RLS harnesses leave two tables behind.** `probe` and `t_results` persist in whatever database they ran against, unprotected and granted to the app role. Harmless in development, and something to remove before either harness is ever pointed at a deployed database.
19. **`ai_actions` has `provider`, `model`, `llm_role`, `prompt_tokens`, `completion_tokens` and `estimated_cost` columns that nothing writes.** `AiActionAuditSink` populates none of them and the `AiAction` entity has no such properties; `LlmUsage` reaches the change report and stops. An empty column is worse than a missing one — it invites the assumption that the run used whatever is configured now. Owed before the eval set compares a local floor against a hosted result. *(ADR 0011, ADR 0013)*
20. **Three environment variables in `.env.example` set nothing — two fixed, and the class of defect is the point.** `OLLAMA_KEEP_ALIVE` was read by no code and passed to no container (Ollama reads it as a *server* variable, and Ollama is not in the compose stack) — now removed from `.env.example` rather than left implying it worked. `LLM_FAST_MODEL`/`LLM_DEEP_MODEL` reached the container as `Llm__Roles__*` while `OllamaOptions` binds `Llm:Ollama:*` — fixed in compose, but the pattern is the point: a documented variable that quietly does nothing outlasts the person who wrote it. Nothing asserts that a configuration key is read by anyone.
21. **`docs/TOOLS.md:187` has `create_event` take a resolved `starts_at`.** Resolving "Saturday 8pm" is calendar arithmetic against today, which non-negotiable 2 (SPEC.md 56.7) forbids the model from doing, and its sibling at `TOOLS.md:64` correctly uses `date_expression` with `TOOLS.md:136` noting the application resolves it. Demonstrated: against the old schema three models returned three wrong dates, off by 7 months, 13 months and nearly 3 years. `data/ollama-toolcall-smoke.json` has been aligned with `OllamaLlmProviderTests`, which was already correct; the published contract still needs a decision. *(M3)*
22. **No CI.** Deliberately deferred. "CI gate" currently means a command someone remembers to run.

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

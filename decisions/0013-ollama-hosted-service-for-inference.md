# 0013. Ollama's hosted service for inference; local stays the fallback

Date: 2026-08-12
Status: Accepted
Amends: ADR 0011's host, not its provider selection

## Context

ADR 0011 chose Ollama running locally, and the reasoning holds: no key, no
budget, no network, and an abstraction exercised by two genuinely different
providers. What it did not weigh is what sustained local inference costs the
machine it runs on.

On an RTX 4070 Laptop the GPU is shared with the Windows compositor. A 20-block
dump is roughly forty seconds of saturation, and the laptop becomes unpleasant to
use for the duration — worse than the latency numbers suggest, because a laptop
under sustained GPU load also throttles its CPU on a shared power budget. This is
not a tuning problem. `OLLAMA_KEEP_ALIVE` addresses idle VRAM, not contention
during inference, and it turned out never to have been wired to anything anyway.

So the question became: move inference off the machine without paying, and
without giving up SPEC.md 40.

That last clause disqualifies almost everything. Free tiers are the tiers that
reserve the right to train on inputs — Google's pricing page states it plainly,
"content used to improve our products" on free versus "content **not** used" on
paid. Groq publishes no policy at all and its 6–15k TPM ceiling collides with
ADR 0011's own projection of ~3060 prompt tokens per block at 17 tools, which
works out to 2–5 blocks per minute. Aggregators that route across many free tiers
inherit the worst policy in the pool, and a "free API" whose operator is not
identified and which publishes no terms is worse than either.

Ollama's hosted service is the exception: it states that prompt and response data
is never logged or trained on, and it speaks the identical `/api/chat` with the
identical request body as the local instance.

## Decision

**Use Ollama's hosted service for inference; keep local as the fallback.**

One provider, not two. The hosted service is the same endpoint and the same
payload, so the difference is a base address and a bearer token:

```bash
OLLAMA_BASE_URL=https://ollama.com
OLLAMA_API_KEY=<from https://ollama.com/settings/keys>
LLM_FAST_MODEL=gemma4:31b
```

Unset those three and the same code talks to `localhost:11434` as before. The
credential's presence is what selects the host — a local instance needs none —
and `OllamaLlmProvider.Name` reports `ollama-cloud` when one is set. ADR 0011
rests on that distinction: "anything a local model tells us about extraction
quality is a floor, not a result", and a floor is only recoverable later if the
record says it was one.

**That record does not exist yet, and the gap is worse than it looks.**
`data/schema.sql` gives `ai_actions` a `provider`, `model`, `llm_role`,
`prompt_tokens`, `completion_tokens` and `estimated_cost` column, and
`AiActionAuditSink` writes **none** of them — the `AiAction` entity has no such
properties. `LlmUsage` reaches the change report on screen and stops there. So
the columns a future reader would trust are silently empty, which is a worse
failure than their absence: an absent column prompts a question, an empty one
invites the assumption that this run was whatever is configured today. Naming
the host in `Name` is therefore necessary but not sufficient; persisting it is
owed before the eval set is used to compare a local floor against a hosted
result. Recorded as a debt in STATUS rather than fixed here, because it belongs
to the audit path (ADR 0004) rather than to provider selection.

**`gemma4:31b` is selected**, from the free tier.

## Measured, 2026-08-12

Against the free tier with one API key. `/api/tags` lists 18 models; **7 are
entitled**. The other 11 return `403 this model requires a subscription` — the
list is not the entitlement, which is worth knowing before designing around a
model name seen in the catalogue.

Run against the project's real contract: `create_event` taking `date_expression`
verbatim, with the system prompt that forbids calendar arithmetic. Required
result is three calls — two `create_shopping_item`, one `create_event`.

| model | result | latency | date_expression |
|---|---|---|---|
| `gemma4:31b` | **pass** | 1.9s | `Saturday 8pm` |
| `nemotron-3-nano:30b` | pass | 1.7s | `saturday 8pm` |
| `minimax-m3` | pass | 2.7s | `Saturday 8pm` |
| `gpt-oss:120b` | **fail** | 3.3s | 1 of 3 calls |
| `gpt-oss:20b` | **fail** | 4.2s | 1 of 3 calls |
| `nemotron-3-super` | not tested | 21.2s to first token | |
| `nemotron-3-ultra` | not tested | 30.4s to first token | |

**Size does not select.** The largest free model failed, and failed in the shape
ADR 0011 predicted — "more single-tool calls where two were needed". It returned
one `create_shopping_item` and silently dropped both the second item and the
event. ADR 0004 makes the tool layer the sole write path, so a dropped call
writes nothing and the change report looks complete. Never adopt a model here
without running the smoke test.

`prompt_eval_count` ranged 240–574 for byte-identical input across these models.
ADR 0011's ~3060-token projection is tokenizer-specific and does not carry over;
re-baseline per model before trusting a cost or latency estimate built on it.

## Consequences

**Easier.** The laptop is usable while a dump processes. The model is roughly an
order of magnitude larger than the local 4B, which raises the floor ADR 0011
deferred the product verdict against — though not to a frontier model, so the
verdict stays deferred.

**Harder.** Inference now needs a network and a credential, which is precisely
what ADR 0011 valued not needing. Local remains configured and working for that
reason, not as a courtesy: a hosted service that is down, throttled, or has
retired a model must not stop development.

**Unmeasured.** Free-tier limits are documented only as "light usage" with one
concurrent model. Roughly 25 requests across this evaluation hit no wall, which
establishes nothing about a 20-block dump. Expect to find the ceiling in use.

**A real risk worth naming.** Hosted models are retired on the provider's
schedule — the documentation lists examples. ADR 0011 makes the eval set the
instrument that separates a floor from a result, and it judges a specific model.
A retired model invalidates that baseline without warning, which is an argument
for recording the model *and* host on every `ai_actions` row rather than assuming
the configured value is what ran.

**Accepting.** Prompts leave the machine. The stated policy is that they are not
logged or trained on, and that statement is the whole basis for this decision —
it is a trust assumption, not a technical guarantee, and it is the reason no other
free option qualified.

## Alternatives considered

**Stay local, tune it** — rejected. `OLLAMA_KEEP_ALIVE` governs idle residency,
not contention during inference, and the pain is during inference. Nothing
configurable moves a shared laptop GPU out of the way.

**A metered paid tier** — deferred, not rejected on merit. At this volume it
would cost between ten cents and two dollars a month, comes with no-training
terms by default, and prompt caching would answer ADR 0011's "cost that actually
scales" section directly — the same 3000 tokens of tool schema re-encoded per
block become a ~0.1x cache read, without batching's loss of per-block error
isolation. Not taken because a paid solution was explicitly out of scope. Revisit
when the free tier's ceiling is actually hit, or when the product verdict needs a
frontier model.

**An aggregator across many free tiers** — rejected. Inherits the weakest data
policy in the pool, and health-based failover means the model can change between
calls, which breaks the eval set's premise that it measures one model.

**A free public gateway with no account** — rejected. Prompts to servers of
unstated ownership under no published terms, for an application whose data ADR
0007 describes as "among the most sensitive a person owns". Its client also
exposes a prompt-in/text-out shape with no tool schemas, so it could not run this
pipeline regardless.

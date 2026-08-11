# 0011. Ollama as the development provider; Anthropic reserved for validation

Date: 2026-08-11
Status: Accepted
Amends: ADR 0003's V0 provider selection

## Context

ADR 0003 put every model call behind `ILLMProvider` with roles resolved from
configuration, and named Anthropic for `fast` and `deep`. That selection assumed
an API key. There isn't one, and there may not be for a while.

Almost none of V0's remaining work needs a frontier model. The schema, the RLS
policies, the tool layer, the dump-file parser, the change report, the auth flow
and the entire HTTP surface are all model-agnostic. Blocking them on billing
would be absurd.

But one thing does need it. V0 exists to answer whether a couple will actually
use this — and that question is only answerable if extraction is good enough
that the change report is worth reading. A 7B model that misclassifies a third
of the blocks would produce a "no" that says nothing about the idea.

So there are two distinct needs, and one provider cannot serve both.

## Decision

**Ollama for development, selected by configuration exactly as ADR 0003
intended.** No new abstraction; `IOllamaProvider` is a second implementation of
the existing interface.

```jsonc
"llm": {
  "roles": {
    "fast":  { "provider": "ollama", "model": "qwen2.5:3b-instruct",  "maxTokens": 1024 },
    "deep":  { "provider": "ollama", "model": "qwen2.5:7b-instruct",  "maxTokens": 4096 }
  },
  "ollama": { "baseUrl": "http://host.docker.internal:11434" }
}
```

Model choice is configuration, not code. Any Ollama model with tool-calling
support works; `qwen2.5` is the default because its function-calling adherence
is the best of the small open models. Swap it in `.env` without touching C#.

**Anthropic stays the target for the validation run.** When a key exists, the
switch is two config lines. Until then, the product question stays explicitly
open rather than being answered badly.

**The eval set is how the two are separated.** `data/eval-cases.jsonl` runs
against whichever provider is configured. Running it against a local model now
establishes a floor; running it against Anthropic later measures the gap. A V0
that underperforms tells us which of the two failed, instead of leaving it
ambiguous.

## Consequences

**Easier.** Development needs no key, no budget and no network. The abstraction
gets exercised by two genuinely different providers early, which is the only
reliable way to discover that an abstraction leaks — a single implementation
always looks clean.

**Harder.** Tool-calling reliability on small local models is materially worse:
more malformed arguments, more invented fields, more single-tool calls where two
were needed. Expect the change report to be noisier and `needs_input` to fire
more often than it will in production.

**The part that matters.** Weak models degrade *quality*, not *safety*. ADR 0004
makes the tool layer the sole write path and validates every argument; ADR 0005
scopes every row at the database. A confused 3B model produces a bad extraction
— never a cross-couple leak, never an unvalidated write, never a row in another
couple's data. This is the first real payoff from those two decisions, and it is
why swapping in a weaker model is a quality trade rather than a risk.

**Accepting.** V0's product verdict is deferred until a frontier model runs the
eval set. Anything a local model tells us about extraction quality is a floor,
not a result.

## Note for Milestone M4

`memories.embedding` is declared `vector(1536)`, matching OpenAI's
`text-embedding-3-small`. Local embedding models have different dimensions —
`nomic-embed-text` is 768, `mxbai-embed-large` is 1024. Since retrieval is
lexical until M4 (ADR 0002) this costs nothing today, but the column width is a
provider commitment and choosing a local embedding model later means a
migration, not a config change.

## Alternatives considered

**Wait for an API key** — rejected. It blocks every model-agnostic task, which
is nearly all of the remaining work.

**A mock provider returning canned responses** — rejected as the only option. It
never exercises real tool-calling, so the first contact with a real model would
come late, when it is expensive to be wrong. Worth having in addition, for fast
deterministic unit tests.

**llama.cpp or LM Studio directly** — rejected. Ollama is already installed, its
model management is one command, and it speaks an OpenAI-compatible API that a
future hosted provider can reuse.

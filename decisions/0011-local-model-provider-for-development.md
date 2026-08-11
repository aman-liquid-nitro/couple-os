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
    "deep":  { "provider": "ollama", "model": "qwen3.5:9b",  "maxTokens": 4096 }
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

## Hardware constraints on this machine

An RTX 4070 Laptop with 8 GB of VRAM, minus what Windows takes for the desktop.
Three consequences, all measured against that number rather than assumed:

**One model serves both roles.** A 7-9B model at Q4 is 4.5-6.6 GB. Holding a
separate small model for `fast` and a larger one for `deep` does not fit, so
Ollama would evict and reload between them — and `fast` runs once per block
while `deep` runs rarely, so most calls would pay a reload. One model is faster
than two here despite being the larger choice. Split only if measurement says to.

**`num_ctx` must be set explicitly on every request.** Ollama's default context
is far below what these models support, and exceeding it does not error — it
truncates. In this system that is the worst possible failure shape: the tail of
a long `shared.md` is cut off, blocks are never seen, and the change report
confidently lists what it did process. The user sees missing entries and no
error, and concludes the extraction is unreliable. An integration test must feed
an oversized block and assert the system chunks or refuses rather than silently
dropping it.

**Ollama version is a correctness dependency, not a preference.** A known defect
had `qwen3.5:9b` emit tool calls as prose instead of invoking them
(ollama/ollama#14745, present in 0.17.7, fixed by #15022). ADR 0004 makes the
tool layer the sole write path, so a model that describes a call rather than
making one writes nothing at all — silently, with a change report showing no
entities created. Pin a floor version in the README and assert tool-call
execution in the provider smoke test rather than trusting it.

`data/ollama-toolcall-smoke.json` is that smoke test: two real tool schemas and
an input that must produce three calls. Run it against any candidate model
before writing code against it.

## Measured, 2026-08-11

RTX 4070 Laptop, 8 GB VRAM. Same prompt, same tools, same input, `temperature: 0`.
Both models produced byte-identical tool calls: two `create_shopping_item`, one
`create_event` carrying `date_expression: "Saturday 8pm"` verbatim.

| run | warm latency | prompt tok/s | gen tok/s | gen tokens | cold load | placement |
|---|---|---|---|---|---|---|
| `qwen3.5:4b` | **1.90s** | 2014 | 65.1 | 102 | 6.20s | 100% GPU |
| `qwen3.5:9b` | 3.27s | 1489 | 36.3 | 102 | 10.53s | 12% CPU / 88% GPU |
| `qwen3.5:4b`, thinking on | 8.79s | 1658 | 67.1 | 571 | 6.70s | 100% GPU |

**`qwen3.5:4b` is selected.** Identical output, 1.71x faster, fully resident,
and it leaves VRAM headroom instead of consuming all of it. The larger model
earns nothing here.

**`"think": false` is mandatory, not tuning.** It cut generated tokens by 82%
and inference time 4.6x, because the thinking trace was 571 of 673 tokens. The
extraction path wants a tool call, not an essay about one.

**`OLLAMA_KEEP_ALIVE` matters more than it looks.** A cold load costs 6.2s, paid
by the first block after any idle period — which is exactly when someone presses
Process having just written their notes. Keep the model resident during a
session.

### The cost that actually scales

The smoke test used 3 tools and spent 660 prompt tokens, of which ~515 were the
tool schemas. Those schemas are identical on every call, and V0 defines 17
tools. Extrapolating linearly:

```text
 3 tools  ->  ~660 prompt tokens  ->  0.33s prompt eval per block
17 tools  -> ~3060 prompt tokens  ->  1.52s prompt eval per block
             a 20-block dump      ->  ~62s
```

Sixty seconds for one Process is not acceptable for the interaction ADR 0009
describes, and the waste is structural: the same 3000 tokens of schema are
re-encoded for every block. This is a projection from one measurement, not an
observation — but it is the right shape to design against before M3.

Two levers, both deferred until measured on real dumps:

- **Filter the tool set per block.** A cheap classification pass with no tool
  schemas attached picks the 2-3 plausible tools, and only those are sent. This
  finally gives ADR 0003's `fast`/`deep` split a real justification — the roles
  differ by prompt shape, not by model, which is convenient now that one model
  serves both.
- **Batch blocks per call.** Amortises the schema cost across many blocks and is
  the larger win by far, at the cost of per-block error isolation: one malformed
  argument can spoil a batch, and `dump_block_entities` needs each entity
  attributed to the block that caused it.

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

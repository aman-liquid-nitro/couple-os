# 0003. Provider abstraction with role-based model routing

Date: 2026-08-11
Status: Accepted

## Context

§22 requires a provider-independent AI layer supporting OpenAI, Anthropic, Gemini, Ollama and OpenAI-compatible endpoints. §50 requires cost control through model routing — cheap models for classification and extraction, strong models for planning and hard reasoning. §40 eventually requires local models for a privacy mode.

The trap is scattering model names through the codebase. `if (model == "gpt-4o-mini")` in a classifier hardcodes both a provider and a price point into domain logic, and every provider change becomes a grep.

## Decision

Two abstractions, deliberately kept apart.

**`ILLMProvider`** — transport. One implementation per provider, responsible only for turning an `LLMRequest` into an `LLMResponse` including tool-call round trips, token accounting, and provider-specific error mapping. Exactly as specified in §22.

**Roles** — intent. Call sites never name a model. They name a role:

```text
fast     Classification, intent detection, extraction, short summaries
deep     Planning, ambiguous questions, multi-step agent workflows
embed    Embedding generation
local    Anything the user has marked as never leaving their infrastructure
```

Configuration maps role to provider and model, so switching providers is a config change with no recompilation:

```jsonc
{
  "Llm": {
    "Roles": {
      "fast":  { "provider": "openai",    "model": "gpt-4o-mini",          "maxTokens": 1024 },
      "deep":  { "provider": "anthropic", "model": "claude-sonnet-4-5",    "maxTokens": 4096 },
      "embed": { "provider": "openai",    "model": "text-embedding-3-small" },
      "local": { "provider": "ollama",    "model": "llama3.1:8b", "baseUrl": "http://localhost:11434" }
    }
  }
}
```

> **Amended by ADR 0011.** Development runs on a local Ollama model; the
> Anthropic selection below is reserved for the validation run, when a key
> exists. The mechanism is unchanged — this is a configuration change, which
> is the whole point of the abstraction.

**V0 uses Anthropic for both reasoning roles** — `fast` and `deep` — because the
whole design leans on tool-calling and instruction-following, and the tool layer
(ADR 0004) is only as safe as the model's willingness to stay inside it. `embed`
stays unconfigured in V0; retrieval is lexical until Milestone 4 (ADR 0002).

Every call records provider, model, role, latency, token counts and estimated cost to `ai_actions`, satisfying §49 and §50's per-couple cost tracking.

## Consequences

**Easier.** Cost tuning becomes a config edit — demote a task from `deep` to `fast` and measure against the 0004 eval suite. Adding a provider means one class. §40's privacy mode is already modelled by the `local` role rather than being a later retrofit.

**Harder.** Roles are a lowest-common-denominator interface; provider-specific features (prompt caching, extended thinking, structured-output modes) need either escape hatches or per-provider capability flags. The abstraction must not become so thin it forbids using what a provider is good at. Mitigation: `LLMRequest` carries an open `ProviderOptions` bag that providers may honour or ignore, and capability probing is explicit rather than assumed.

**Accepting.** Prompts are tuned per role, not per model, so swapping the model behind a role can shift behaviour. The eval suite (`data/eval-cases.jsonl`) is the guard: a role's model may not change without a passing eval run.

## Alternatives considered

**Semantic Kernel / LangChain** — rejected for V0. Both bring a large surface area and an opinionated agent model that conflicts with §26's insistence on controlled workflows over autonomous loops. Revisit if the hand-rolled tool loop becomes a maintenance burden.

**Direct SDK calls, no abstraction** — rejected. Contradicts §22 and §56.4, and makes the local-model privacy mode a rewrite.

**Model named at each call site** — rejected. Cost policy would be scattered across the codebase instead of living in one config file.

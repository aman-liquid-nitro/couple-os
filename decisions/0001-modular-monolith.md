# 0001. Modular monolith over microservices

Date: 2026-08-11
Status: Accepted

## Context

Couple OS spans thirteen modules (§5): identity, memory, inbox, tasks, shopping, household, finance, calendar, goals, planning, notifications, AI, privacy. That count invites a service-per-module decomposition.

Two facts argue against it. First, the product is unvalidated — §53 defines success as *two people finding it useful for one week*, and §56.1 forbids building the whole product before testing the core interaction. Second, nearly every AI request touches many modules at once: §43's context pipeline reads memory, tasks, events, expenses and goals to answer a single message. In a microservice world that is a distributed join on the hot path of an interactive chat.

The deployment target is a single VPS serving two users (§41), not a scaling problem.

## Decision

One ASP.NET Core deployable, internally partitioned by module, following §42's layout: `Api` / `Application` / `Domain` / `Infrastructure` / `AI` / `Workers`. Modules communicate through in-process application services behind explicit interfaces, never through HTTP. One PostgreSQL database, one schema; foreign keys across module boundaries are allowed.

Background jobs (§27 proactive analysis, notification delivery) run as a hosted service inside the same process for V0–V1, extracted to a separate Workers process only when job volume or failure isolation demands it.

## Consequences

**Easier.** Transactional consistency across modules, so §25's multi-action flow commits atomically or not at all. The entire §43 context pipeline becomes one database round trip. Local development is `docker compose up` with two containers. Moving a module boundary costs a namespace rename, not an API version.

**Harder.** Boundaries are enforced by discipline rather than by the network, and without care the Application layer becomes a mud ball. Mitigation: no module may reference another module's Domain types directly — cross-module access goes through the target module's application service interface, and this is enforced by an architecture test in `CoupleOS.UnitTests`. A runaway background job can also degrade API latency.

**Accepting.** No independent scaling or deployment per module. For a two-person household that is not a cost.

## Alternatives considered

**Microservices per module** — rejected. Distributed transactions for §25, network hops inside §43's pipeline, and operational overhead that §41 explicitly warns against ("do not optimize for Kubernetes or complex infrastructure initially").

**Serverless functions** — rejected. Cold starts sit directly on an interactive chat path, and connection pooling for EF Core and pgvector fits the model poorly.

**Separate AI service from day one** — deferred, not rejected. §22's `ILLMProvider` abstraction and 0003's routing config keep this extraction cheap if the AI layer later needs its own scaling or its own hardware for local models (§40).

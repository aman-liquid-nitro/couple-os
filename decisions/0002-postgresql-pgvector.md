# 0002. PostgreSQL with pgvector, no dedicated vector database

Date: 2026-08-11
Status: Accepted

## Context

§10 requires hybrid retrieval: structured filtering, semantic search, recency, importance, scope and visibility, combined. §33 recommends PostgreSQL with `pgvector` and warns against a dedicated vector database before scale requires one.

The volume argument is decisive. A couple generating fifty memories a month produces roughly 600 rows a year. Even a decade of heavy use stays under 10,000 vectors — a scale at which an exact-scan index outperforms any approximate index, and at which a separate vector store is pure operational overhead.

The correctness argument matters more. §28 forbids retrieving private partner data merely because it is semantically relevant. A separate vector store means visibility filtering happens *after* the nearest-neighbour search, in application code, on a result set that has already crossed a trust boundary. That is precisely the shape of a privacy bug.

## Decision

PostgreSQL is the only datastore. The `vector` extension is enabled in the initial migration and `memories.embedding vector(1536)` exists from day one as a nullable column.

Retrieval is a single SQL query that applies visibility predicates in the same `WHERE` clause as the similarity ordering, so a row the caller may not see is never a candidate.

Index strategy is deliberately staged: no vector index at all until the corpus exceeds ~10,000 rows (exact scan is faster below that), then HNSW. A `pg_trgm` GIN index and a `tsvector` column serve lexical search from the start.

Redis, Pinecone, Qdrant, Weaviate and Elasticsearch are out of scope until a measured problem justifies one.

## Consequences

**Easier.** One backup, one restore, one connection string, one transaction. Visibility enforcement and similarity ranking are provably inseparable because they are the same query. Row-level security (0005) covers the semantic path for free.

**Harder.** Embedding generation still needs a pipeline (batching, retries, backfill after model changes) — pgvector solves storage, not lifecycle. Changing embedding model dimensions requires a column migration and a full re-embed; the `memory_embeddings` table therefore records `model` and `dimensions` so a re-embed is detectable rather than silent.

**Accepting.** If Couple OS ever becomes multi-tenant at scale, this decision gets revisited. That is a good problem to have and an explicitly deferred one.

## Alternatives considered

**Dedicated vector DB (Pinecone/Qdrant)** — rejected. Post-hoc visibility filtering (see Context), a second system to back up and secure, and a network hop holding the couple's most private data.

**SQLite + sqlite-vec** — rejected despite fitting the single-VPS deployment. §41 commits to PostgreSQL, and concurrent background jobs (§27) plus row-level security are materially better served by Postgres.

**In-memory index rebuilt at startup** — rejected. Restart cost grows with the corpus, and it forfeits RLS.

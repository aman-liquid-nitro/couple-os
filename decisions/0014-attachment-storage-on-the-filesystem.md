# ADR 0014 — Attachment bytes on the filesystem, behind an interface

Date: 2026-08-14
Status: Accepted
Milestone: M5

## Context

V0 stores attachments and links them to whatever records the surrounding block
produced ([V0_SCOPE.md](../docs/V0_SCOPE.md)). `data/schema.sql` already decided
half of it: `attachments.storage_key` is `text` with a unique index, and there is
no `bytea` column. So the bytes live somewhere the database points at rather than
somewhere the database holds.

That leaves where. Three options were real.

**A `bytea` column.** Would mean changing the schema under ADR 0012, and would
put a receipt photo into every `SELECT *` and every backup of a table that is
otherwise small. PostgreSQL handles it, and the operational cost lands on the one
thing this project cannot easily replace.

**Object storage.** S3, R2, or a MinIO container. Correct at scale and wrong at
this one: it adds a credential, a bucket policy and a fourth container to a stack
whose whole claim is `docker compose up`, in service of a couple who will upload
a few receipts a week. SPEC.md §40's privacy stance also makes a hosted bucket a
decision worth taking deliberately rather than as plumbing.

**The local filesystem**, on a named volume, behind an interface.

## Decision

**The filesystem, on a named Docker volume, behind `IAttachmentStore`.**

The interface is the point as much as the filesystem is. `IAttachmentStore` has
two methods — write a stream and get back a key, read a key back — and neither
mentions a path. Moving to object storage later is a second implementation and a
registration, not a change to anything that calls it.

Four properties of the implementation are load-bearing:

**The key is opaque and namespaced by couple.** `{couple_id}/{uuid}` — not the
filename. A user-supplied filename in a path is a traversal waiting to happen,
and the same name uploaded twice would collide. The couple prefix means a
mis-scoped read is at least a mis-scoped *path*, and deleting a couple's data is
one directory rather than a query.

**The checksum is computed while streaming, not afterwards.** Reading the file
back to hash it doubles the I/O and, worse, hashes what was written rather than
what arrived — which is exactly the difference a corrupt write consists of.

**The write is to a temporary name, then a move.** A partial write must never be
readable as an attachment. `File.Move` within one volume is atomic, so a key
either resolves to a complete file or does not resolve.

**Nothing is served with a Content-Type the uploader chose.** The column records
what the browser claimed, because that is worth knowing, and the download path
ignores it: `application/octet-stream`, `Content-Disposition: attachment`,
`X-Content-Type-Options: nosniff`. A private-data application that serves
user-uploaded files inline is an XSS vector aimed at the one browser session that
can read everything.

## Consequences

**Deployment gains a stateful directory.** The volume is now something a deploy
has to preserve, alongside `coupleos-pgdata` and `coupleos-dataprotection`. It is
also created the same way, for the reason recorded in the Dockerfile: Docker
seeds a named volume from the image directory it covers, ownership included, but
only if that directory exists — so the path is created and chowned at build time
or the container cannot write to its own volume.

**The database and the disk can disagree.** A row whose file is missing, or a
file with no row. The row is written inside the same transaction as everything
else the request does, and the bytes are written *first*: an orphaned file is
wasted space, and an orphaned row is a broken link on a page. Neither is
reconciled in V0, and nothing deletes anything, so the failure mode is bounded.
Recorded as debt rather than solved, because a reconciliation job with no
deletion path to reconcile against is a job with nothing to do.

**Authorization is not this interface's business.** The store takes a key and
returns bytes; it has no idea whose they are. What stops a partner reading a
private attachment is the same thing that stops them reading a private memory —
the row is invisible under row-level security, so the key is never resolved in
the first place, and the answer is a 404 rather than a 403. An absence, never a
hint (ADR 0005, and `search_memory`'s rendering for the same rule stated in
prose).

**No OCR, and the column says so.** `ocr_status` stays `not_attempted` and is not
mapped, so nothing can set it by accident. A model that claimed to have read a
receipt would be fabricating (SPEC.md §46), and eval case `attach-001` asserts
the absence.

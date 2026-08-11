# 0010. Server-rendered UI with htmx for V0

Date: 2026-08-11
Status: Accepted
Supersedes: SPEC.md §41's frontend stack, for V0 only

## Context

SPEC.md §41 specifies Next.js, React and TypeScript, and ARCHITECTURE.md drew the
system that way. That was the right default when the interface was unknown.

It is now known. ADR 0009 settled V0's entire surface area:

```text
sign-in            one email field
shared.md          a textarea, a quick-add line, a Process button
change report      a list
private chat       a message list and an input
read surface       a list grouped by type
```

Four screens, none of them rich. Against that, a separate frontend costs a second
deployable, CORS, cookie auth across origins, a Node build pipeline in
production, and a hand-maintained API client that must stay in sync with the
server — a category of work that exists only because the client is separate.

SPEC.md §41 also says to choose the simplest thing that works and warns against
optimizing for infrastructure early. Applied to the frontend, that argues against
a SPA for a two-person private app.

## Decision

**Razor Pages, server-rendered, with htmx for interactivity.** One deployable.

- Cookie session auth (ADR 0007) works with no CORS and no token plumbing
- `Process` is a form post that swaps the change report into the page
- Quick-add is an `hx-post` appending to Inbox without a full page load
- Private chat streams via SSE using htmx's SSE extension
- Alpine.js only if something genuinely needs client state; the default is none
- No Node in the production image

Presentation stays strictly separate from application services, so `CoupleOS.Api`
can expose JSON endpoints for a future mobile client or SPA without touching
domain code. This decision is about what V0 *renders*, not about coupling.

## Consequences

**Easier.** One thing to deploy, one auth mechanism, no API client to keep in
sync, no build step between writing a page and seeing it. M0 gets shorter by
roughly a whole workstream. Server-side rendering also keeps the couple's data
out of client-side state entirely, which fits the product's privacy stance.

**Harder.** htmx has a ceiling, and V1's dashboard (SPEC.md §35) may want richer
interaction than it comfortably gives. The dump editor is a plain textarea — no
syntax highlighting, no inline autocomplete, no rich editing. Optimistic UI is
awkward, so `Process` will feel like a request rather than an instant update,
which puts more weight on it being fast.

**Accepting.** If V1 needs a real SPA, we add one then and this decision costs
almost nothing to reverse — the application layer never knew about the
presentation layer. Reversing the other direction, having built a SPA first,
would have cost the whole frontend.

## Alternatives considered

**Next.js + React + TypeScript, per SPEC.md §41** — rejected for V0, genuinely
open again at V1. Correct choice for a rich dashboard; overhead for four lists
and a textarea.

**Blazor Server** — rejected. Keeps everything in C#, which is attractive, but it
holds a SignalR circuit open per user for a UI that is mostly static, and it is a
larger framework bet than htmx for no V0 benefit.

**API only, no UI — test through a client and a synced file** — rejected. Fastest
to the validation question and fails it: ADR 0009 already identifies capture
friction as the thing most likely to make V0 fail for an interface reason rather
than an idea reason.

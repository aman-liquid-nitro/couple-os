# 0007. Magic-link authentication for V1

Date: 2026-08-11
Status: Accepted

## Context

§41 leaves authentication open between OAuth, magic links and email/password, asking for "the simplest secure option for V1". §29 requires hashed passwords *if* password authentication is used — an obligation avoidable by not using passwords.

The user population is two people with stable email addresses who sign in rarely on a small number of devices. The data is among the most sensitive a person owns: private notes, finances, gift surprises, relationship plans.

## Decision

**Magic links.** No passwords are stored, ever.

Flow: user enters email → single-use token generated → link emailed → clicking it establishes a session.

Parameters:

```text
Token             32 bytes from a CSPRNG, base64url encoded
Storage           SHA-256 hash only; the plaintext token exists only in the email
Lifetime          15 minutes
Uses              exactly one; consumed atomically, replay returns the same error as expiry
Rate limit        3 requests per email per 15 min, 10 per IP per hour
Enumeration       identical response and timing whether or not the address exists
Session           httpOnly, Secure, SameSite=Lax cookie; 30-day rolling expiry
Revocation        session table, so "sign out everywhere" is a delete
```

Partner invitation (Milestone 1) reuses the same mechanism: the invite *is* a magic link carrying a couple-join claim, so there is one authentication path rather than two.

In development, links are written to the console and to a local maildev container. No email provider is required to run the project locally.

## Consequences

**Easier.** No password hashing, no reset flow, no credential-stuffing exposure, no breach class involving leaked password hashes — §29's password obligations disappear rather than being satisfied. Rare sign-ins make the email round trip a minor cost. The invitation flow comes almost free.

**Harder.** Email becomes a hard dependency and a single point of failure for access; a provider outage locks both users out. Mail deliverability becomes an operational concern (SPF, DKIM, DMARC on the sending domain). Email account compromise is now full account compromise — an accepted risk given that a password reset flow would have the same property. Links in email are also a phishing-training hazard, so the email states plainly that Couple OS will never ask for anything beyond clicking the link.

**Accepting.** Sign-in requires access to email at that moment. With a 30-day rolling session and two users on known devices, this should surface a handful of times a year.

## Alternatives considered

**Email + password** — rejected. Adds hashing, reset flows, breach exposure and a password the user will reuse, in exchange for removing an email round trip they will experience a few times a year.

**OAuth with Google** — rejected for V1, reconsider at V3. Attractive because it stores no credentials and pre-authorizes §39's Google Calendar integration. Rejected now because it ties a deliberately private product to a third-party identity, requires OAuth client setup before the project can run locally, and §40's privacy mode points the other way.

**Passkeys / WebAuthn** — strong candidate, deferred. Better security and better UX than magic links, but device registration and recovery flows are meaningfully more work than V1 justifies. Revisit at V2; the session model above does not need to change to accommodate it.

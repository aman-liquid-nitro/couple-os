using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Conversation;

/// <summary>
/// One exchange: what the person said, what was said back, and what actually
/// changed.
/// </summary>
/// <param name="Changes">
/// Every tool call the turn made, successes and refusals alike, in the same
/// <see cref="CaptureChange"/> shape the shared surface's report uses. Carried
/// separately from <paramref name="Reply"/> rather than folded into it, because
/// the reply is the model's prose and this is the record. A model that writes
/// "added that to your list" without calling the tool is the failure SPEC.md 46
/// forbids, and the only way the screen can contradict it is by having both.
/// </param>
public sealed record ConversationTurn(
    ConversationMessage Said,
    ConversationMessage Reply,
    IReadOnlyList<CaptureChange> Changes)
{
    /// <summary>
    /// What this turn cost. Null when no model was reached — a turn that failed
    /// before the call is free, and recording zero tokens for it would be
    /// indistinguishable from a call that returned nothing.
    /// </summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>
    /// True when the reply is the model reporting it could not answer, rather than
    /// an answer. The screen says so; the turn is still in the transcript, because
    /// a thread that silently drops a failed exchange leaves the person retyping
    /// into a gap.
    /// </summary>
    public bool Failed { get; init; }
}

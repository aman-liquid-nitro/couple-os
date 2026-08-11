using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>Writes the ai_actions row for every dispatched call.</summary>
public sealed class AiActionAuditSink(CoupleOsDbContext dbContext) : IToolAuditSink
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var action = new AiAction
        {
            CoupleId = entry.Context.CoupleId,
            UserId = entry.Context.UserId,
            ToolName = entry.ToolName,
            Arguments = entry.Arguments.GetRawText(),
            Outcome = ToActionOutcome(entry.Outcome),
            IdempotencyKey = entry.Context.IdempotencyKey,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            ErrorMessage = entry.Error,
            LatencyMs = (int)entry.Duration.TotalMilliseconds,
        };

        _dbContext.AiActions.Add(action);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Throws rather than guessing.
    ///
    /// ToolOutcome.ConfirmationRequired has no label in the action_outcome enum
    /// (data/schema.sql). Substituting a near-enough value would make the audit
    /// trail describe something that did not happen, and the audit trail is the
    /// only reason to trust the change report. Every V0 tool is tier None so
    /// this is unreachable today; the first Confirm-tier tool needs a migration
    /// before it ships.
    /// </summary>
    private static ActionOutcome ToActionOutcome(ToolOutcome outcome) => outcome switch
    {
        ToolOutcome.Success => ActionOutcome.Success,
        ToolOutcome.ValidationFailed => ActionOutcome.ValidationFailed,
        ToolOutcome.Unauthorized => ActionOutcome.Unauthorized,
        ToolOutcome.ExecutionFailed => ActionOutcome.ExecutionFailed,
        ToolOutcome.CancelledByUser => ActionOutcome.CancelledByUser,

        ToolOutcome.ConfirmationRequired => throw new NotSupportedException(
            "action_outcome has no 'confirmation_required' label. Add one by migration before " +
            "shipping a Confirm-tier tool; do not map this onto another outcome."),

        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown tool outcome."),
    };
}

using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.UnitTests;

/// <summary>Records the row it was asked to create, and writes nothing.</summary>
internal sealed class RecordingTaskWriter : ITaskWriter
{
    public List<TaskItem> Written { get; } = [];

    public TaskItem? Last => Written.Count == 0 ? null : Written[^1];

    public Task AddAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        Written.Add(task);

        return Task.CompletedTask;
    }
}

/// <summary>
/// A clock fixed to a known instant in a known zone.
///
/// Fixed, because the alternative is a test suite that starts failing on a
/// Friday. <c>DateExpressionResolverTests</c> already pins "now" for exactly this
/// reason — the eval set's hard-coded absolute dates are the other end of that
/// lesson (STATUS debt 21).
/// </summary>
internal sealed class FixedCoupleClock(
    DateTimeOffset? now = null,
    string zoneId = "Asia/Kolkata",
    bool recognised = true) : ICoupleClock
{
    /// <summary>Thursday, 13 August 2026, 10:00 in Asia/Kolkata.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(5.5));

    public Task<CoupleTime> NowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CoupleTime(
            now ?? DefaultNow,
            TimeZoneInfo.FindSystemTimeZoneById(zoneId),
            recognised));
}

/// <summary>
/// Answers with one partner, or with nobody — the state a couple is in between
/// being created and the invitation being accepted.
/// </summary>
internal sealed class StubPartnerLookup(Guid? partner) : IPartnerLookup
{
    public Guid? AskedAboutCouple { get; private set; }

    public Guid? AskedAboutUser { get; private set; }

    public Task<Guid?> FindPartnerAsync(
        Guid coupleId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        AskedAboutCouple = coupleId;
        AskedAboutUser = userId;

        return Task.FromResult(partner);
    }
}

internal static class ToolArguments
{
    internal static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static ToolInvocation Invocation(
        string json,
        Guid coupleId,
        Guid userId,
        Visibility visibility = Visibility.SharedCouple) =>
        new(
            Json(json),
            new ToolExecutionContext
            {
                CoupleId = coupleId,
                UserId = userId,
                Visibility = visibility,
            });
}

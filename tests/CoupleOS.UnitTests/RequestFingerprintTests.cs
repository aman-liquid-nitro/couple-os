using CoupleOS.Application.Capture;
using CoupleOS.Evals;
using Xunit;
using Xunit.Abstractions;

namespace CoupleOS.UnitTests;

/// <summary>
/// The eval set judges the tool descriptions, and until now nothing versioned
/// them.
///
/// <c>CapturePrompt.Version</c> exists because "a gate that measures a string
/// literal which can change silently measures nothing". A tool's
/// <c>Description</c> and its argument descriptions are in every request the
/// eval set makes; they are what four of M3's five red cases were fixed by
/// editing, and one of those edits made the model omit a required field on two
/// cases that had been passing. Nothing in the repository would have let a later
/// run tell "the model got worse" from "somebody reworded a schema"
/// (STATUS debt 42).
///
/// So the request has a version too, and it is a digest rather than a hand-kept
/// number — because the edit that most needs one is the small wording change
/// nobody thinks of as a change. The digest moves on its own; the constant here
/// is what a person has to look at, which is where the deliberate act belongs.
/// </summary>
public sealed class RequestFingerprintTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static string Current() =>
        RequestFingerprint.Of(CapturePrompt.Version, CapturePrompt.System, ToolCatalogueTests.Registered());

    [Fact]
    public void The_request_the_eval_set_judges_has_not_changed_without_being_noticed()
    {
        var actual = Current();

        _output.WriteLine($"prompt      : {CapturePrompt.Version}");
        _output.WriteLine($"fingerprint : {actual}");

        Assert.True(
            actual == EvalCatalogueVersion.Current,
            $"The prompt or a tool description changed: {EvalCatalogueVersion.Current} → {actual}. " +
            "That is allowed and it is not free — every eval number recorded against the old digest " +
            "was measured against a different request. Re-run the eval gate, and if the results still " +
            "hold, set EvalCatalogueVersion.Current to the new value in the same commit as the edit.");
    }

    /// <summary>
    /// The digest has to move for a wording change and stay still for anything
    /// the model never sees. Both halves matter: a fingerprint that ignored
    /// descriptions would be decoration, and one that moved on whitespace would
    /// be noise nobody reads.
    /// </summary>
    [Fact]
    public void The_fingerprint_moves_with_a_reworded_description_and_not_with_the_prompt_version_alone()
    {
        var tools = ToolCatalogueTests.Registered();

        var baseline = RequestFingerprint.Of(CapturePrompt.Version, CapturePrompt.System, tools);
        var reordered = RequestFingerprint.Of(CapturePrompt.Version, CapturePrompt.System, [.. tools.AsEnumerable().Reverse()]);

        // Registration order is not guaranteed by anything, so it must not be
        // able to move the digest on its own.
        Assert.Equal(baseline, reordered);

        var edited = RequestFingerprint.Of(
            CapturePrompt.Version,
            CapturePrompt.System + " 7. And one more thing.",
            tools);

        Assert.NotEqual(baseline, edited);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoupleOS.Application.Tools;

namespace CoupleOS.Evals;

/// <summary>
/// What the model was asked, in eight characters.
///
/// <para><c>CapturePrompt.Version</c> exists because "a gate that measures a
/// string literal which can change silently measures nothing". A tool's
/// <c>Description</c> and its argument descriptions are in every request the
/// eval set makes, are what four of M3's five red cases were fixed by editing,
/// and carried no version at all — so nothing in the repository would let a
/// later run tell "the model got worse" from "somebody reworded a schema"
/// (STATUS debt 42). M3 demonstrated the failure rather than predicting it: one
/// edit to a description made a model omit a required field on two cases that
/// had been passing.</para>
///
/// <para>Hashed rather than versioned by hand, for the reason a hand-maintained
/// version fails: it is one more thing to remember, and the edit that needs it
/// most is the small wording change nobody thinks of as a change. The digest
/// moves on its own; <see cref="EvalCatalogueVersion.Current"/> is the value
/// somebody has to look at and update, which is where the deliberate act
/// belongs.</para>
/// </summary>
public static class RequestFingerprint
{
    /// <summary>
    /// Over the prompt and the whole catalogue. Tools are sorted by name so that
    /// registration order — which nothing guarantees — cannot move the digest
    /// without a word changing.
    /// </summary>
    public static string Of(string promptVersion, string promptText, IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var builder = new StringBuilder()
            .Append(promptVersion).Append('\n')
            .Append(promptText).Append('\n');

        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            builder
                .Append(tool.Name).Append('\n')
                .Append(tool.Description).Append('\n')

                // The schema's own text, which is where the argument descriptions
                // are. GetRawText rather than a re-serialisation, so that a
                // formatting change the model never sees cannot move the digest.
                .Append(tool.ParametersSchema.GetRawText()).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return Convert.ToHexString(digest)[..8].ToLower(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The fingerprint the eval expectations were last agreed against.
///
/// Checked in, and asserted by a test. A prompt or description edit therefore
/// fails the build until somebody writes the new digest here — which is the
/// point: the edit is cheap, and deciding that the eval numbers either side of
/// it are still comparable is not.
/// </summary>
public static class EvalCatalogueVersion
{
    /// <summary>
    /// gemma4:31b, CapturePrompt 2026-08-14.1, eight tools.
    /// Update deliberately, with the eval run that justifies it.
    /// </summary>
    public const string Current = "unset";
}

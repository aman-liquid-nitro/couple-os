using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>
/// Where the text arrived from. ADR 0009 makes this the sole determinant of
/// visibility — not the words, not the model's judgment.
///
/// Earlier drafts had the model infer privacy from sentence shape. "She
/// mentioned she likes that bag" and "she mentioned she wants to visit her
/// parents" are the same shape with opposite answers, and one of those
/// mistakes cannot be taken back.
/// </summary>
public enum CaptureSurface
{
    /// <summary>shared.md — both partners can see it.</summary>
    SharedFile,

    /// <summary>The private thread — only the author.</summary>
    PrivateThread,
}

public static class CaptureSurfaceExtensions
{
    /// <summary>
    /// The mapping lives here and nowhere else, so visibility is derived rather
    /// than passed around as a parameter someone could get wrong.
    /// </summary>
    public static Visibility ToVisibility(this CaptureSurface surface) => surface switch
    {
        CaptureSurface.SharedFile => Visibility.SharedCouple,
        CaptureSurface.PrivateThread => Visibility.PrivateUser,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown capture surface."),
    };
}

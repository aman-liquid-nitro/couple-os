using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// Where a row came from, derived from the surface that produced it.
///
/// <para>Every row this system wrote but one claimed <c>source = 'chat'</c>, and
/// most of them were typed into a file. <c>data_source</c> defaults to
/// <c>chat</c> on four tables and no tool mapped the column, so a task, an event
/// or an expense written into <c>shared.md</c> recorded the provenance of a
/// conversation that never happened (STATUS debt 39). <c>create_memory</c> was
/// the exception, and it was the exception for a reason worth keeping: a
/// memory's provenance is the one that gets read back, because ADR 0006 requires
/// an inferred memory to be surfaced with where it came from.</para>
///
/// <para>Written once here rather than four times, for the same reason
/// <see cref="ToolDate"/> is one place: four tools each deriving the same
/// mapping is four chances for one of them to be a milestone behind. That is not
/// hypothetical — it is exactly how this column got wrong in the first place,
/// with one tool setting it and three inheriting a default nobody had looked
/// at.</para>
///
/// <para>The mapping is from <b>visibility</b> rather than from a surface name,
/// which reads like a shortcut and is not. Visibility is what ADR 0009 says the
/// surface hands down, and it is the only thing about the surface a tool is
/// given — a tool that could ask "which screen am I on" would be a tool that
/// could be told the wrong answer.</para>
/// </summary>
public static class ToolSource
{
    public static DataSource Of(Visibility visibility) =>
        visibility == Visibility.PrivateUser ? DataSource.Chat : DataSource.UserInput;
}

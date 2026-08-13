namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>data_source</c> enum — how a row came to exist. Five tables carry it and
/// all five default it to <c>chat</c>.
///
/// Mapped because <c>create_memory</c> is the first tool to write it deliberately.
/// The default is wrong for the shared file and was wrong before this enum
/// existed: every task, event and expense written from <c>shared.md</c> claims
/// <c>chat</c>, because no tool maps the column and the database default fills it
/// in. That is STATUS debt 39, and it is paid here for memories alone rather than
/// across five tables mid-milestone — a memory is the row whose provenance is
/// actually read back, since ADR 0006 requires an inferred one to be surfaced with
/// where it came from.
///
/// <see cref="UserInput"/> is a person typing into a file or a form;
/// <see cref="Chat"/> is a conversational turn. The distinction is the capture
/// surface, which is the same thing visibility is derived from (ADR 0009) — so it
/// comes from the surface and never from the text.
///
/// Declaration order matches the database's, as <see cref="TaskItemKind"/>
/// explains.
/// </summary>
public enum DataSource
{
    UserInput,
    Chat,
    ImportedDocument,
    Manual,
    SystemInference,
}

namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>data_source</c> enum — how a row came to exist. Four tables carry it and
/// all four default it to <c>chat</c>.
///
/// The default is wrong for the shared file, and for three milestones only
/// <c>create_memory</c> overrode it — so every task, event and expense typed into
/// <c>shared.md</c> claimed the provenance of a conversation that never happened
/// (STATUS debt 39). Paid across all four now, from one place
/// (<c>ToolSource</c>), and the property is <c>required</c> on every entity that
/// carries it: the CLR default here is <see cref="UserInput"/> where the column's
/// is <see cref="Chat"/>, so an omission would not inherit the schema's answer,
/// it would silently contradict it.
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

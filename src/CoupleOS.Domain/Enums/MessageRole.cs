namespace CoupleOS.Domain.Enums;

/// <summary>
/// Mirrors the message_role enum in data/schema.sql.
///
/// Not to be confused with <c>LlmMessageRole</c> in the Application project,
/// which is the wire vocabulary of one model call and has no <see cref="Tool"/>
/// member. They look alike and mean different things: this one is what a row in
/// the couple's transcript is, and it outlives the call that produced it. Two
/// enums rather than one shared enum, because the day a provider adds a role of
/// its own is not the day the couple's stored history should gain a value.
///
/// As with <see cref="Visibility"/> and <see cref="DumpFileKind"/>, member names
/// carry no [PgName]: Domain references nothing, so the labels come from
/// Npgsql's snake-case translator and the mapping tests check that convention
/// rather than trusting it.
/// </summary>
public enum MessageRole
{
    /// <summary>What the person typed.</summary>
    User,

    /// <summary>What was said back. Authorless — no human wrote it.</summary>
    Assistant,

    /// <summary>
    /// Present in the schema and unwritten in V0. The prompt lives in
    /// <c>ChatPrompt</c>, versioned, and storing a copy of it per thread would
    /// make the transcript the second place a prompt lives.
    /// </summary>
    System,

    /// <summary>
    /// A tool's own turn. Unwritten in V0: what a tool did is recorded in
    /// ai_actions, which carries the arguments, the outcome and the entity, and a
    /// second rendering of the same event in the transcript would be a second
    /// account to keep in agreement.
    /// </summary>
    Tool,
}

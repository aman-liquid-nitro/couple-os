namespace CoupleOS.Domain.Enums;

/// <summary>
/// ADR 0005's three scopes, superseding SPEC.md 9's two-value list.
///
/// Member names are NOT decorated with [PgName]: that attribute lives in
/// Npgsql, and Domain references nothing. Npgsql's default snake-case
/// translator maps these to the database labels private_user, shared_couple
/// and system. That is a convention rather than a declaration, so
/// VisibilityMappingTests asserts each label round-trips — a silent mismatch
/// here would mean the wrong rows were visible to the wrong person.
/// </summary>
public enum Visibility
{
    /// <summary>Visible only to the owner. Never enters the partner's context.</summary>
    PrivateUser,

    /// <summary>Visible to both members of the couple.</summary>
    SharedCouple,

    /// <summary>Internal rows. Never enters an LLM prompt, never shown as fact.</summary>
    System,
}

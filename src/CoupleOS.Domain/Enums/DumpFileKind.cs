namespace CoupleOS.Domain.Enums;

/// <summary>
/// Mirrors the dump_file_kind enum in data/schema.sql.
///
/// The two kinds are the two visibility scopes, and the schema's
/// dump_files_kind_matches_visibility constraint says so: shared implies
/// shared_couple with no owner, private implies private_user with one. There is
/// no third combination, which is what makes ADR 0009's "the surface determines
/// the scope" a database property rather than an application habit.
///
/// As with <see cref="Visibility"/>, member names carry no [PgName]: Domain
/// references nothing, so the labels come from Npgsql's snake-case translator
/// and DumpEnumMappingTests checks that convention rather than trusting it.
/// </summary>
public enum DumpFileKind
{
    /// <summary>shared.md — one per couple, both partners read and write.</summary>
    Shared,

    /// <summary>
    /// One per member. Present in the schema and unused in V0: ADR 0009 chose
    /// chat as the private surface and kept this so a private notebook can be
    /// added later without a migration.
    /// </summary>
    Private,
}

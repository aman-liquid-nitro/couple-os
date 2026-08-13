using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Puts every class that writes a couple's shared file in one collection, so
/// they run one at a time.
///
/// `shared.md` is one row per couple by construction — dump_files_one_shared_per_couple
/// — so two test classes using the fixture couple are two writers of the same
/// row. xUnit runs classes in parallel by default, and the assertions that break
/// are the ones worth having: a test that reads a version, expects the next save
/// to advance it by one, and finds it advanced by three.
///
/// This is the same reason IdentityCollection exists, arrived at from the other
/// direction: there, classes deleted each other's fixtures; here, they overwrite
/// each other's file. In both cases the failures move between runs, which is the
/// expensive kind of red.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedFileCollection
{
    public const string Name = "shared-file";
}

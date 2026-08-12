using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Puts every identity test class in one collection, so they run one at a time.
///
/// They share five tables and each one clears them, which is fine sequentially and
/// destructive in parallel: xUnit runs test classes concurrently by default, so
/// without this one class's cleanup would delete another's fixtures mid-test and
/// the failures would move around between runs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityCollection
{
    public const string Name = "identity";
}

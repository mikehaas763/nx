namespace MsbuildAnalyzer.Models;

/// <summary>
/// What test discovery found, and what it deliberately left out.
/// </summary>
/// <remarks>
/// The exclusion counts exist so the analyzer can say so. A test class that
/// silently gets no target of its own is the one failure mode of this feature
/// that is invisible from the outside — the tests still run under the ordinary
/// test target, so nothing fails, and CI just quietly stops covering them in
/// the split run.
/// </remarks>
public sealed record TestDiscoveryResult
{
    public required List<TestUnit> Units { get; init; }

    /// <summary>
    /// Test classes nested inside another type. Excluded because the platform's
    /// encoding of nested type names in a filter is unconfirmed.
    /// </summary>
    public int SkippedNested { get; init; }

    /// <summary>
    /// Generic test classes, and (in method mode) generic test methods. Excluded
    /// because their names are mangled in both filter syntaxes.
    /// </summary>
    public int SkippedGeneric { get; init; }

    public static TestDiscoveryResult Empty => new() { Units = [] };
}

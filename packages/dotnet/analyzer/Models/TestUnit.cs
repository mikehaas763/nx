namespace MsbuildAnalyzer.Models;

/// <summary>
/// The granularity at which a test project's tests are split into Nx targets.
/// </summary>
public enum SplitBy
{
    /// <summary>
    /// One target per test class. The default: coarse enough that process
    /// startup stays negligible for ordinary suites.
    /// </summary>
    Class,

    /// <summary>
    /// One target per test method. Worth opting into when each method carries
    /// its own expensive fixture (spinning up a distributed app, a database, a
    /// browser), because then grouping methods saves nothing and only serializes
    /// work that could have run on separate agents.
    /// </summary>
    Method
}

/// <summary>
/// One unit of test atomization: either a test class or a single test method.
/// </summary>
public sealed record TestUnit
{
    /// <summary>Enclosing namespace, or empty for the global namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>The declaring test class's simple name.</summary>
    public required string ClassName { get; init; }

    /// <summary>The test method's name, or null when the unit is a whole class.</summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Whether MSTest was told these tests must not run in parallel, via
    /// <c>[DoNotParallelize]</c> at assembly, class, or method level.
    /// </summary>
    public bool DoNotParallelize { get; init; }

    /// <summary>
    /// Whether the method is data-driven (<c>[DataRow]</c>/<c>[DynamicData]</c>),
    /// meaning it expands into several test cases at run time.
    /// </summary>
    /// <remarks>
    /// Not needed to build the filter — <c>FullyQualifiedName=</c> is exact and
    /// MSTest folds data rows under the method's own identity — but recorded so
    /// diagnostics can explain a leaf that reports more cases than its name
    /// suggests.
    /// </remarks>
    public bool HasDataRows { get; init; }

    /// <summary>Fully-qualified name of the declaring class.</summary>
    public string ClassFqn =>
        string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";

    /// <summary>
    /// Stable identifier for this unit. Used as the Nx target-name suffix, as
    /// the per-unit test-results subdirectory, and as the dedupe key.
    /// </summary>
    public string Id => MethodName is null ? ClassFqn : $"{ClassFqn}.{MethodName}";

    /// <summary>
    /// The command-line arguments that restrict a test run to this unit.
    /// </summary>
    /// <remarks>
    /// The single place a filter expression is constructed, so that the
    /// contingency for either filter syntax is a one-line change here.
    ///
    /// Class units use the platform-level <c>--treenode-filter</c>, which works
    /// for any Microsoft.Testing.Platform framework.
    ///
    /// Method units deliberately use <c>--filter</c> instead. The treenode
    /// equivalent would have to be <c>/*/Ns/Class/Method</c>, which may not match
    /// the expanded nodes of a <c>[DataRow]</c> method; widening it to
    /// <c>Method*</c> to compensate would make <c>LoginTest*</c> also match
    /// <c>LoginTestWithMfa</c>, running that test in two leaves at once.
    /// <c>FullyQualifiedName=</c> is an exact match, so neither problem exists —
    /// at the cost of being framework-provided (MSTest) rather than
    /// platform-level.
    ///
    /// Values are quoted because Nx runs commands through a shell, which would
    /// otherwise glob-expand the <c>*</c> in a treenode filter against the
    /// working directory. Quoting is safe without escaping: C# namespace and
    /// member names are limited to letters, digits and underscores, so a quote
    /// character can never appear inside one.
    /// </remarks>
    public string[] FilterArgs => MethodName is null
        ? ["--treenode-filter", Quote($"/*/{(string.IsNullOrEmpty(Namespace) ? "*" : Namespace)}/{ClassName}/*")]
        : ["--filter", Quote($"FullyQualifiedName={Id}")];

    private static string Quote(string value) => $"\"{value}\"";
}

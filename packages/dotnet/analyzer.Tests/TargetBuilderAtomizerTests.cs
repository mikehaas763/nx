using MsbuildAnalyzer.Models;
using MsbuildAnalyzer.Utilities;
using Xunit;

namespace MsbuildAnalyzer.Tests;

/// <summary>
/// Unit tests for the split test targets.
///
/// Like the other TargetBuilder tests these drive the pure-logic path with a
/// property dictionary mirroring what MSBuild would return, and inject the
/// discovered test units directly rather than evaluating a real project.
/// </summary>
public class TargetBuilderAtomizerTests
{
    private static readonly string WorkspaceRoot = Path.Combine(Path.GetTempPath(), "nx-dotnet-ws");
    private static readonly string ProjectDirectory = Path.Combine(WorkspaceRoot, "apps", "IntegrationTests");

    private static readonly List<TestUnit> TwoClasses =
    [
        new() { Namespace = "Acme.Integration", ClassName = "CheckoutTests" },
        new() { Namespace = "Acme.Integration", ClassName = "LoginTests" }
    ];

    private static PluginOptions Options(
        string? ciTargetName = "test-ci",
        string? ciGroupName = "TEST (CI)",
        SplitBy splitBy = SplitBy.Class) => new()
        {
            TestCiTargetName = ciTargetName,
            TestCiGroupName = ciGroupName,
            TestCiSplitBy = splitBy
        };

    private static BuildTargetsResult Build(
        PluginOptions? options = null,
        List<TestUnit>? units = null,
        bool isMtp = true,
        bool isTest = true,
        Dictionary<string, string>? properties = null,
        string? projectDirectory = null) =>
        TargetBuilder.BuildTargets(
            projectName: "IntegrationTests",
            fileName: "IntegrationTests.csproj",
            isTest: isTest,
            isExe: true,
            packageRefs: [],
            properties: properties ?? new Dictionary<string, string>(),
            projectDirectory: projectDirectory ?? ProjectDirectory,
            workspaceRoot: WorkspaceRoot,
            options: options ?? Options(),
            nxJson: null,
            directoryBuildInputs: [],
            isMtp: isMtp,
            discoverTestUnits: _ => units ?? TwoClasses);

    private static string[] Args(Target target) => target.Options?.Args ?? [];

    // --- Opt-in gating ------------------------------------------------------

    [Fact]
    public void WithoutCiTargetName_NoSplitTargetsAreEmitted()
    {
        var result = Build(Options(ciTargetName: null));

        Assert.DoesNotContain(result.Targets.Keys, name => name.StartsWith("test-ci"));
        Assert.Null(result.TargetGroups);
        Assert.False(result.DerivedFromSources);
        // The ordinary test target is untouched.
        Assert.True(result.Targets.ContainsKey("test"));
    }

    [Fact]
    public void WithoutMtp_NoSplitTargetsAreEmitted()
    {
        // Splitting needs the platform's filtering options, so a VSTest-only
        // project cannot be split even when asked.
        var result = Build(isMtp: false);

        Assert.DoesNotContain(result.Targets.Keys, name => name.StartsWith("test-ci"));
        Assert.False(result.DerivedFromSources);
    }

    [Fact]
    public void NonTestProject_IsNeverSplit()
    {
        var result = Build(isTest: false);

        Assert.DoesNotContain(result.Targets.Keys, name => name.StartsWith("test-ci"));
    }

    [Fact]
    public void WithNoDiscoveredUnits_EmitsNoParentEither()
    {
        // An empty group would show in the UI, and a no-op parent with no
        // dependencies would pass while running nothing.
        var result = Build(units: []);

        Assert.DoesNotContain(result.Targets.Keys, name => name.StartsWith("test-ci"));
        Assert.Null(result.TargetGroups);
        Assert.False(result.DerivedFromSources);
    }

    // --- Shape of the emitted targets --------------------------------------

    [Fact]
    public void EmitsOneLeafPerUnitPlusANoopParent()
    {
        var result = Build();

        Assert.True(result.Targets.ContainsKey("test-ci--Acme.Integration.LoginTests"));
        Assert.True(result.Targets.ContainsKey("test-ci--Acme.Integration.CheckoutTests"));
        Assert.Equal("nx:noop", result.Targets["test-ci"].Executor);
        Assert.Null(result.Targets["test-ci"].Command);
        Assert.True(result.DerivedFromSources);
    }

    [Fact]
    public void ParentPointsBackAtTheNonAtomizedTarget()
    {
        // Nx core keys the Nx Cloud requirement, .env resolution and
        // target-defaults matching off this.
        var result = Build();

        Assert.Equal("test", result.Targets["test-ci"].Metadata?.NonAtomizedTarget);
    }

    [Fact]
    public void ParentPointsAtARenamedTestTarget()
    {
        var options = Options();
        options.TestTargetName = "unit-test";

        var result = Build(options);

        Assert.Equal("unit-test", result.Targets["test-ci"].Metadata?.NonAtomizedTarget);
    }

    [Fact]
    public void ParentDependsOnEveryLeafWithForwarding()
    {
        var result = Build();

        var dependencies = (result.Targets["test-ci"].DependsOn ?? [])
            .OfType<TargetDependency>()
            .ToList();

        Assert.Equal(2, dependencies.Count);
        Assert.All(dependencies, dependency =>
        {
            // Without forwarding, flags typed on the parent stop at the no-op.
            Assert.Equal("forward", dependency.Params);
            Assert.Equal("forward", dependency.Options);
        });
        Assert.Contains(dependencies, d => d.Target == "test-ci--Acme.Integration.LoginTests");
    }

    [Fact]
    public void LeavesReuseTheTestTargetInputsAndBuildDependency()
    {
        var result = Build();

        var test = result.Targets["test"];
        var leaf = result.Targets["test-ci--Acme.Integration.LoginTests"];

        Assert.Equal(test.Inputs, leaf.Inputs);
        Assert.Equal(test.Cache, leaf.Cache);
        // --no-build only works because the build output is a dependency.
        Assert.Equal(test.DependsOn, leaf.DependsOn);
        Assert.Contains("--no-build", Args(leaf));
    }

    [Fact]
    public void TargetGroupListsTheParentFirst()
    {
        var result = Build();

        var group = Assert.Contains("TEST (CI)", result.TargetGroups!);
        Assert.Equal("test-ci", group[0]);
        Assert.Equal(3, group.Count);
    }

    [Fact]
    public void GroupNameFallsBackWhenNoneIsProvided()
    {
        var result = Build(Options(ciGroupName: null));

        Assert.True(result.TargetGroups!.ContainsKey("TEST-CI (CI)"));
    }

    // --- Filters ------------------------------------------------------------

    [Fact]
    public void ClassLeavesFilterByTreeNodeWithTheNamespaceSegment()
    {
        var result = Build();

        var args = Args(result.Targets["test-ci--Acme.Integration.LoginTests"]);
        var index = Array.IndexOf(args, "--treenode-filter");

        Assert.True(index >= 0);
        Assert.Equal("\"/*/Acme.Integration/LoginTests/*\"", args[index + 1]);
    }

    [Fact]
    public void MethodLeavesFilterByExactFullyQualifiedName()
    {
        var result = Build(
            Options(splitBy: SplitBy.Method),
            units:
            [
                new() { Namespace = "Acme", ClassName = "Tests", MethodName = "LoginTest" },
                new() { Namespace = "Acme", ClassName = "Tests", MethodName = "LoginTestWithMfa" }
            ]);

        var args = Args(result.Targets["test-ci--Acme.Tests.LoginTest"]);
        var index = Array.IndexOf(args, "--filter");

        Assert.True(index >= 0);
        // Exact match, so LoginTestWithMfa does not also run in this leaf.
        Assert.Equal("\"FullyQualifiedName=Acme.Tests.LoginTest\"", args[index + 1]);
        Assert.DoesNotContain("--treenode-filter", args);
    }

    [Fact]
    public void FilterValuesAreQuoted()
    {
        // Nx runs commands through a shell, which would glob-expand the * in a
        // treenode filter against the working directory.
        var result = Build();

        Assert.All(
            result.Targets.Where(pair => pair.Key.StartsWith("test-ci--")),
            pair => Assert.Contains(Args(pair.Value), arg => arg.StartsWith('"') && arg.Contains('*')));
    }

    [Fact]
    public void EveryLeafFailsLoudlyWhenItsFilterMatchesNothing()
    {
        var result = Build();

        Assert.All(
            result.Targets.Where(pair => pair.Key.StartsWith("test-ci--")),
            pair =>
            {
                var args = Args(pair.Value);
                var index = Array.IndexOf(args, "--minimum-expected-tests");
                Assert.True(index >= 0);
                Assert.Equal("1", args[index + 1]);
            });
    }

    [Fact]
    public void PlatformArgumentsFollowASeparator()
    {
        var result = Build();

        var args = Args(result.Targets["test-ci--Acme.Integration.LoginTests"]);
        var separator = Array.IndexOf(args, "--");

        Assert.True(separator >= 0);
        // Everything the platform needs must be after it; SDK flags before.
        Assert.True(Array.IndexOf(args, "--treenode-filter") > separator);
        Assert.True(Array.IndexOf(args, "--no-build") < separator);
    }

    // --- Results directories ------------------------------------------------

    [Fact]
    public void EachLeafClaimsItsOwnResultsDirectory()
    {
        // Sharing the project's TestResults directory would make replaying one
        // leaf from cache restore the others' results too.
        var result = Build();

        Assert.Equal(
            ["{projectRoot}/TestResults/Acme.Integration.LoginTests"],
            result.Targets["test-ci--Acme.Integration.LoginTests"].Outputs);
        Assert.Equal(
            ["{projectRoot}/TestResults/Acme.Integration.CheckoutTests"],
            result.Targets["test-ci--Acme.Integration.CheckoutTests"].Outputs);
    }

    [Fact]
    public void ResultsDirectoryArgumentIsRelativeToTheWorkingDirectory()
    {
        var result = Build();

        var args = Args(result.Targets["test-ci--Acme.Integration.LoginTests"]);
        var index = Array.IndexOf(args, "--results-directory");

        Assert.Equal("\"TestResults/Acme.Integration.LoginTests\"", args[index + 1]);
    }

    [Fact]
    public void WorkspaceAnchoredResultsDirectoryWalksBackUp()
    {
        // The artifacts-output layout puts results above the project directory,
        // so the CLI argument has to climb out of it.
        var properties = new Dictionary<string, string>
        {
            ["UseArtifactsOutput"] = "true",
            ["ArtifactsPath"] = Path.Combine(WorkspaceRoot, "artifacts")
        };

        var result = Build(properties: properties);
        var leaf = result.Targets["test-ci--Acme.Integration.LoginTests"];
        var args = Args(leaf);
        var index = Array.IndexOf(args, "--results-directory");

        Assert.Equal(
            ["{workspaceRoot}/artifacts/TestResults/IntegrationTests/Acme.Integration.LoginTests"],
            leaf.Outputs);
        Assert.Equal(
            "\"../../artifacts/TestResults/IntegrationTests/Acme.Integration.LoginTests\"",
            args[index + 1]);
    }

    [Fact]
    public void ParentKeepsTheWholeResultsDirectoryAsItsOutput()
    {
        var result = Build();

        Assert.Equal(result.Targets["test"].Outputs, result.Targets["test-ci"].Outputs);
    }

    [Fact]
    public void ReportFilenameIsUniquePerLeafAndTargetFramework()
    {
        var result = Build();

        var args = Args(result.Targets["test-ci--Acme.Integration.LoginTests"]);
        var index = Array.IndexOf(args, "--report-trx-filename");

        Assert.Equal("\"Acme.Integration.LoginTests_{tfm}.trx\"", args[index + 1]);
    }

    // --- Parallelism --------------------------------------------------------

    [Fact]
    public void LeavesRunConcurrentlyByDefault()
    {
        var result = Build();

        Assert.Null(result.Targets["test-ci--Acme.Integration.LoginTests"].Parallelism);
        Assert.Null(result.Targets["test-ci"].Parallelism);
    }

    [Fact]
    public void DoNotParallelizeUnitsAreMarkedSerial()
    {
        var result = Build(units:
        [
            new() { Namespace = "Acme", ClassName = "Serial", DoNotParallelize = true },
            new() { Namespace = "Acme", ClassName = "Concurrent" }
        ]);

        Assert.False(result.Targets["test-ci--Acme.Serial"].Parallelism);
        Assert.Null(result.Targets["test-ci--Acme.Concurrent"].Parallelism);
        // Some units may still run concurrently, so the group is not serial.
        Assert.Null(result.Targets["test-ci"].Parallelism);
    }

    [Fact]
    public void WhenEveryUnitIsSerialTheGroupIsToo()
    {
        var result = Build(units:
        [
            new() { Namespace = "Acme", ClassName = "A", DoNotParallelize = true },
            new() { Namespace = "Acme", ClassName = "B", DoNotParallelize = true }
        ]);

        Assert.False(result.Targets["test-ci"].Parallelism);
    }
}

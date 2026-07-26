using MsbuildAnalyzer.Models;

namespace MsbuildAnalyzer.Utilities;

/// <summary>
/// Splits a test target into one target per test unit, plus a no-op parent that
/// depends on all of them.
/// </summary>
public static partial class TargetBuilder
{
    /// <summary>
    /// How <c>dotnet test</c> is being driven, which changes the surrounding
    /// flags (not the filter, which is identical in both).
    /// </summary>
    public enum TestRunnerMode
    {
        /// <summary>
        /// The default. <c>dotnet test</c> drives VSTest, which bridges to
        /// Microsoft.Testing.Platform when TestingPlatformDotnetTestSupport is
        /// set. Platform arguments go after a literal <c>--</c>.
        /// </summary>
        VsTestBridge,

        /// <summary>
        /// .NET 10+ with <c>global.json</c> selecting the platform directly.
        /// A different CLI surface, so restore is handled by the build target
        /// this leaf already depends on rather than by a flag here.
        /// </summary>
        PlatformCli
    }

    /// <summary>
    /// Adds the split targets for a project, or nothing at all when the project
    /// declares no test units.
    /// </summary>
    /// <returns>The target group, or null when nothing was added.</returns>
    private static Dictionary<string, List<string>>? AddAtomizedTestTargets(
        Dictionary<string, Target> targets,
        List<TestUnit> units,
        Target baseTestTarget,
        PluginOptions options,
        TestRunnerMode mode,
        Dictionary<string, string> properties,
        string projectName,
        string projectDirectory,
        string workspaceRoot,
        string fileName)
    {
        var ciTargetName = options.TestCiTargetName!;

        // A project with no discoverable units gets no parent either — an
        // otherwise-empty group would show up in the UI and a no-op parent with
        // no dependencies would silently pass while testing nothing.
        if (units.Count == 0)
        {
            return null;
        }

        var technologies = ProjectUtilities.GetTechnologies(fileName);
        var groupName = options.TestCiGroupName ?? $"{ciTargetName.ToUpperInvariant()} (CI)";
        var dependsOn = new List<object>();
        var groupMembers = new List<string>();

        foreach (var unit in units)
        {
            var targetName = $"{ciTargetName}--{unit.Id}";
            var resultsPaths = GetAtomizedTestResultsPaths(
                properties, projectName, projectDirectory, workspaceRoot, unit.Id);

            // A results directory outside the workspace cannot be declared as an
            // Nx output, so its cache entry would be wrong. The base test target
            // already returns null in that case; follow it rather than emitting
            // a leaf Nx cannot cache correctly.
            if (resultsPaths is null)
            {
                return null;
            }

            var (nxOutputPath, cwdRelativePath) = resultsPaths.Value;

            targets[targetName] = baseTestTarget with
            {
                Options = new TargetOptions
                {
                    Cwd = baseTestTarget.Options?.Cwd,
                    Args = BuildAtomizedArgs(unit, cwdRelativePath, mode)
                },
                Outputs = [nxOutputPath],
                Parallelism = unit.DoNotParallelize ? false : null,
                Metadata = new TargetMetadata
                {
                    Description = $"Run .NET tests in {unit.Id}",
                    Technologies = technologies
                }
            };

            // params/options forwarding is what lets flags typed on the parent
            // (`nx test-ci my-proj --framework=net8.0`) reach each leaf.
            dependsOn.Add(new TargetDependency
            {
                Target = targetName,
                Params = "forward",
                Options = "forward"
            });
            groupMembers.Add(targetName);
        }

        targets[ciTargetName] = new Target
        {
            Executor = "nx:noop",
            Cache = baseTestTarget.Cache,
            Inputs = baseTestTarget.Inputs,
            Outputs = baseTestTarget.Outputs,
            DependsOn = [.. dependsOn],
            // If every unit must run serially then so must the group as a whole;
            // otherwise Nx could still schedule the parent alongside them.
            Parallelism = units.All(unit => unit.DoNotParallelize) ? false : null,
            Metadata = new TargetMetadata
            {
                Description = "Run .NET tests in CI",
                Technologies = technologies,
                NonAtomizedTarget = options.TestTargetName
            }
        };

        // The parent is listed first so the group reads as "the thing you run,
        // then what it expands into".
        groupMembers.Insert(0, ciTargetName);

        return new Dictionary<string, List<string>> { [groupName] = groupMembers };
    }

    /// <summary>
    /// Builds the argument list for a single split test task.
    /// </summary>
    /// <remarks>
    /// Argument order matters. Nx assembles the final command as
    /// <c>&lt;command&gt; &lt;unknownOptions&gt; &lt;options.args&gt;
    /// &lt;__unparsed__&gt;</c>, so flags forwarded from the parent land before
    /// the <c>--</c> here and reach the SDK, while anything a user puts after
    /// <c>nx … --</c> lands after it and reaches the test platform.
    /// </remarks>
    private static string[] BuildAtomizedArgs(
        TestUnit unit,
        string cwdRelativeResultsPath,
        TestRunnerMode mode)
    {
        var args = new List<string> { "--no-build" };

        if (mode == TestRunnerMode.VsTestBridge)
        {
            args.Add("--no-restore");
        }

        args.Add("--");
        args.AddRange(unit.FilterArgs);
        args.AddRange(["--results-directory", $"\"{cwdRelativeResultsPath}\""]);
        args.AddRange(["--report-trx", "--report-trx-filename", $"\"{unit.Id}_{{tfm}}.trx\""]);

        // Without this a filter that matches nothing is a silent pass. Exit code
        // 9 instead makes a discovery bug loud on the first CI run.
        args.AddRange(["--minimum-expected-tests", "1"]);

        return [.. args];
    }
}

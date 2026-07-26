using MsbuildAnalyzer.Models;

namespace MsbuildAnalyzer.Utilities;

/// <summary>
/// Builds Nx target configurations for .NET projects.
/// </summary>
public static partial class TargetBuilder
{
    /// <summary>
    /// Builds all applicable targets for a .NET project.
    /// </summary>
    public static BuildTargetsResult BuildTargets(
        string projectName,
        string fileName,
        bool isTest,
        bool isExe,
        List<PackageReference> packageRefs,
        Dictionary<string, string> properties,
        string projectDirectory,
        string workspaceRoot,
        PluginOptions options,
        NxJsonConfig? nxJson,
        List<string> directoryBuildInputs,
        bool isMtp = false,
        Func<SplitBy, List<TestUnit>>? discoverTestUnits = null)
    {
        var targets = new Dictionary<string, Target>();
        Dictionary<string, List<string>>? targetGroups = null;

        // Determine the appropriate input for production builds
        var productionInput = GetProductionInput(nxJson);

        AddBuildTarget(targets, projectName, fileName, isTest, properties, projectDirectory, workspaceRoot, options, productionInput, directoryBuildInputs);
        AddBuildReleaseTarget(targets, projectName, fileName, isTest, properties, projectDirectory, workspaceRoot, options, productionInput, directoryBuildInputs);

        if (isTest)
        {
            AddTestTarget(targets, projectName, fileName, packageRefs, properties, projectDirectory, workspaceRoot, options, productionInput, directoryBuildInputs);

            if (options.TestCiTargetName is not null && discoverTestUnits is not null)
            {
                if (!isMtp)
                {
                    // Splitting relies on the platform's filtering options, so
                    // there is no way to honor the request here. Say so rather
                    // than quietly producing an unsplit project.
                    Console.Error.WriteLine(
                        $"@nx/dotnet: cannot split tests for '{projectName}' because it does not use " +
                        "Microsoft.Testing.Platform. Set <EnableMSTestRunner>true</EnableMSTestRunner> " +
                        "and <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>, " +
                        "or use the MSTest.Sdk project SDK.");
                }
                else
                {
                    targetGroups = AddAtomizedTestTargets(
                        targets,
                        discoverTestUnits(options.TestCiSplitBy),
                        targets[options.TestTargetName],
                        options,
                        GetTestRunnerMode(workspaceRoot),
                        properties,
                        projectName,
                        projectDirectory,
                        workspaceRoot,
                        fileName);
                }
            }
        }

        // restore/clean/watch/run intentionally omit Directory.* inputs — they don't declare an
        // Inputs array, and adding one here would narrow Nx's default-input fallback should a
        // user enable caching on them later.
        AddRestoreTarget(targets, fileName, options);
        AddCleanTarget(targets, fileName, isTest, options);
        AddWatchTarget(targets, fileName, options);

        if (isExe)
        {
            AddPublishTarget(targets, projectName, fileName, isTest, properties, projectDirectory, workspaceRoot, options, productionInput, directoryBuildInputs);
            AddRunTarget(targets, fileName, options);
        }

        if (!isExe && !isTest)
        {
            AddPackTarget(targets, projectName, fileName, properties, projectDirectory, workspaceRoot, options, productionInput, directoryBuildInputs);
        }

        return new BuildTargetsResult
        {
            Targets = targets,
            TargetGroups = targetGroups,
            DerivedFromSources = targetGroups is not null
        };
    }

    /// <summary>
    /// Determines how <c>dotnet test</c> will be driven for this workspace.
    /// </summary>
    /// <remarks>
    /// .NET 10 lets a workspace select Microsoft.Testing.Platform directly via
    /// <c>global.json</c>, which presents a different CLI surface than the
    /// default VSTest bridge. The pre-.NET-10 <c>dotnet.config</c> form was
    /// removed before release and is deliberately not read.
    /// </remarks>
    private static TestRunnerMode GetTestRunnerMode(string workspaceRoot)
    {
        var globalJsonPath = Path.Combine(workspaceRoot, "global.json");
        if (!File.Exists(globalJsonPath))
        {
            return TestRunnerMode.VsTestBridge;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(globalJsonPath));
            var runner = document.RootElement.TryGetProperty("test", out var test) &&
                         test.TryGetProperty("runner", out var value)
                ? value.GetString()
                : null;

            return string.Equals(runner, "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                ? TestRunnerMode.PlatformCli
                : TestRunnerMode.VsTestBridge;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // An unreadable or malformed global.json is not this analyzer's to
            // report; the SDK will complain far more usefully. Assume the
            // default mode.
            return TestRunnerMode.VsTestBridge;
        }
    }

    /// <summary>
    /// Determines the appropriate input for production builds.
    /// Returns "production" if it exists in nx.json's namedInputs, otherwise "default".
    /// </summary>
    private static string GetProductionInput(NxJsonConfig? nxJson)
    {
        if (nxJson?.NamedInputs != null && nxJson.NamedInputs.ContainsKey("production"))
        {
            return "production";
        }

        return "default";
    }
}

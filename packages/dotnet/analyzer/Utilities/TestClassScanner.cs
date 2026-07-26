using System.Collections.Concurrent;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MsbuildAnalyzer.Models;

namespace MsbuildAnalyzer.Utilities;

/// <summary>
/// Discovers the test classes and methods a project declares, so each can become
/// its own Nx target.
/// </summary>
/// <remarks>
/// This is a syntax-only pass: sources are parsed but never compiled, so there is
/// no semantic model, no reference resolution, and no need for the project to
/// build. That keeps it fast enough to run during project-graph construction, at
/// the cost of two documented blind spots — tests inherited from a base class,
/// and code inside <c>#if</c> regions (parsed as disabled text, since no
/// preprocessor symbols are defined).
///
/// Anything not discovered here is not lost: it still runs under the project's
/// ordinary non-atomized test target.
/// </remarks>
public static class TestClassScanner
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    /// <summary>
    /// Scans the C# sources MSBuild assigned to a project.
    /// </summary>
    /// <remarks>
    /// Reads the <c>Compile</c> item group rather than globbing the project
    /// directory, so <c>&lt;Compile Remove&gt;</c>, linked files, generated
    /// sources and <c>DefaultItemExcludes</c> are all honored without
    /// reimplementing MSBuild's item semantics.
    /// </remarks>
    public static List<TestUnit> Scan(ProjectInstance project, SplitBy splitBy)
    {
        var paths = project
            .GetItems("Compile")
            .Select(item => item.GetMetadataValue("FullPath"))
            .Where(path => !string.IsNullOrEmpty(path) &&
                           path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sources = new ConcurrentBag<string>();
        Parallel.ForEach(paths, path =>
        {
            try
            {
                sources.Add(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A Compile item can point at a file that is not readable (a
                // generated source not yet produced, a stale link). Skipping it
                // costs us its targets, which still run under the non-atomized
                // target; failing the whole graph would be far worse.
                Console.Error.WriteLine(
                    $"@nx/dotnet: could not read '{path}' while discovering tests: {ex.Message}");
            }
        });

        return ScanSources(sources, splitBy);
    }

    /// <summary>
    /// Extracts test units from already-loaded source text.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Scan"/> so the discovery rules can be tested
    /// against inline sources without an MSBuild evaluation.
    /// </remarks>
    public static List<TestUnit> ScanSources(IEnumerable<string> sources, SplitBy splitBy)
    {
        var materialized = sources as IReadOnlyList<string> ?? sources.ToList();

        // [assembly: DoNotParallelize] applies to the whole assembly but may be
        // declared in any file — commonly a shared AssemblyInfo.cs, which
        // contains no test classes of its own — so it has to be resolved across
        // every source before any class is examined.
        //
        // The substring check short-circuits before parsing, which in the
        // overwhelmingly common case where nothing mentions the attribute costs
        // one scan of the text instead of a second parse of every file.
        var assemblyDoNotParallelize = materialized.Any(source =>
            source.Contains("DoNotParallelize", StringComparison.Ordinal) &&
            DeclaresAssemblyDoNotParallelize(source));

        var units = new ConcurrentBag<TestUnit>();

        Parallel.ForEach(materialized, source =>
        {
            foreach (var unit in ScanSource(source, splitBy, assemblyDoNotParallelize))
            {
                units.Add(unit);
            }
        });

        // Deduplicating by Id is what collapses `partial` classes declared across
        // several files into a single class unit, while still letting their
        // methods surface as distinct method units.
        //
        // Ordering must be deterministic: target names derive from these, and an
        // unstable order would change the project graph hash on every run.
        return units
            .GroupBy(unit => unit.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate(MergeDuplicates))
            .OrderBy(unit => unit.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Two declarations of the same unit (partial class halves) may disagree
    /// about their attributes; take the union so a <c>[DoNotParallelize]</c> on
    /// either half is honored.
    /// </summary>
    private static TestUnit MergeDuplicates(TestUnit left, TestUnit right) => left with
    {
        DoNotParallelize = left.DoNotParallelize || right.DoNotParallelize,
        HasDataRows = left.HasDataRows || right.HasDataRows
    };

    private static bool DeclaresAssemblyDoNotParallelize(string source) =>
        CSharpSyntaxTree.ParseText(source, ParseOptions)
            .GetCompilationUnitRoot()
            .AttributeLists
            .Where(list => list.Target?.Identifier.ValueText == "assembly")
            .Any(list => HasAttribute(list, "DoNotParallelize"));

    private static IEnumerable<TestUnit> ScanSource(
        string source,
        SplitBy splitBy,
        bool assemblyDoNotParallelize)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetCompilationUnitRoot();

        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsAtomizableTestClass(declaration))
            {
                continue;
            }

            var classDoNotParallelize =
                assemblyDoNotParallelize || HasAttribute(declaration.AttributeLists, "DoNotParallelize");

            var ns = GetNamespace(declaration);
            var className = declaration.Identifier.ValueText;

            if (splitBy == SplitBy.Class)
            {
                yield return new TestUnit
                {
                    Namespace = ns,
                    ClassName = className,
                    DoNotParallelize = classDoNotParallelize
                };
                continue;
            }

            foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
            {
                // Generic test methods would need their type arguments encoded
                // (and commas %2C-escaped) in the FullyQualifiedName filter.
                // Excluded rather than guessed at.
                if (method.TypeParameterList is not null)
                {
                    continue;
                }

                if (!HasAttribute(method.AttributeLists, "TestMethod", "DataTestMethod"))
                {
                    continue;
                }

                yield return new TestUnit
                {
                    Namespace = ns,
                    ClassName = className,
                    MethodName = method.Identifier.ValueText,
                    DoNotParallelize =
                        classDoNotParallelize || HasAttribute(method.AttributeLists, "DoNotParallelize"),
                    HasDataRows = HasAttribute(method.AttributeLists, "DataRow", "DynamicData")
                };
            }
        }
    }

    private static bool IsAtomizableTestClass(ClassDeclarationSyntax declaration)
    {
        // Nested classes are excluded: the platform encodes them into the class
        // segment in a form we have not confirmed, so filtering on the outer name
        // alone risks matching nothing.
        if (declaration.Parent is not (BaseNamespaceDeclarationSyntax or CompilationUnitSyntax))
        {
            return false;
        }

        // An abstract class has no tests of its own; its concrete subclasses
        // carry their own [TestClass] in practice.
        if (declaration.Modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            return false;
        }

        // Generic classes are name-mangled in both filter syntaxes.
        if (declaration.TypeParameterList is not null)
        {
            return false;
        }

        return HasAttribute(declaration.AttributeLists, "TestClass");
    }

    /// <summary>
    /// Builds the dotted namespace for a declaration, joining nested namespace
    /// blocks outermost-first. Handles both block and file-scoped forms.
    /// </summary>
    private static string GetNamespace(SyntaxNode node) =>
        string.Join('.', node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(ns => ns.Name.ToString())
            .Reverse());

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, params string[] names) =>
        lists.Any(list => HasAttribute(list, names));

    /// <summary>
    /// Matches an attribute by simple name, ignoring any qualification and the
    /// optional <c>Attribute</c> suffix — so <c>[TestClass]</c>,
    /// <c>[TestClassAttribute]</c>, <c>[MSTest.TestClass]</c> and
    /// <c>[global::Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]</c>
    /// are all recognized.
    /// </summary>
    private static bool HasAttribute(AttributeListSyntax list, params string[] names) =>
        list.Attributes.Any(attribute =>
        {
            var name = attribute.Name switch
            {
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => attribute.Name.ToString()
            };

            if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
            {
                name = name[..^"Attribute".Length];
            }

            return names.Contains(name, StringComparer.Ordinal);
        });
}

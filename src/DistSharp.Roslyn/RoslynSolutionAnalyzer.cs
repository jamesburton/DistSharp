using System.Runtime.CompilerServices;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Roslyn.Internal;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DistSharp.Roslyn;

/// <summary>Analyses a .NET solution or project using Roslyn and yields <see cref="ExtractedSymbol"/> records.</summary>
public sealed class RoslynSolutionAnalyzer : ISolutionAnalyzer
{
    private static readonly object MsBuildRegistrationLock = new();
    private static bool msBuildRegistered;

    private readonly ILogger<RoslynSolutionAnalyzer> logger;

    /// <summary>Initializes a new instance of the <see cref="RoslynSolutionAnalyzer"/> class.</summary>
    /// <param name="logger">Logger for analysis diagnostics. Pass <see cref="NullLogger{T}.Instance"/> when not needed.</param>
    public RoslynSolutionAnalyzer(ILogger<RoslynSolutionAnalyzer> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ExtractedSymbol> AnalyzeAsync(
        string solutionPath,
        SolutionAnalysisOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (sender, args) =>
            this.logger.LogWarning("Roslyn workspace diagnostic: {Diagnostic}", args.Diagnostic.Message);

        var projects = await LoadProjectsAsync(workspace, solutionPath, cancellationToken).ConfigureAwait(false);

        var excludeSet = new HashSet<string>(options.ExcludeNamespaces, StringComparer.Ordinal);
        var rootDir = ResolveRootDirectory(solutionPath);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.IncludeTests && TestProjectDetector.IsTestProject(project))
            {
                this.logger.LogDebug("Skipping test project: {Project}", project.Name);
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.IncludeGenerated && IsGenerated(tree))
                {
                    continue;
                }

                var relativePath = ToRelativePath(rootDir, tree.FilePath);
                var semantic = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

                var extractor = new SymbolExtractor(semantic, relativePath, ns => NamespaceIncluded(ns, excludeSet));

                foreach (var symbol in extractor.Extract(root, cancellationToken))
                {
                    if (options.MinComplexity > 0 && symbol.Kind == "method" && symbol.Complexity < options.MinComplexity)
                    {
                        continue;
                    }

                    yield return symbol;
                }
            }
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (msBuildRegistered)
        {
            return;
        }

        lock (MsBuildRegistrationLock)
        {
            if (msBuildRegistered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            msBuildRegistered = true;
        }
    }

    private static async Task<IReadOnlyList<Project>> LoadProjectsAsync(MSBuildWorkspace workspace, string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            var slns = Directory.GetFiles(path, "*.sln", SearchOption.AllDirectories);
            if (slns.Length > 0)
            {
                var solution = await workspace.OpenSolutionAsync(slns[0], cancellationToken: cancellationToken).ConfigureAwait(false);
                return solution.Projects.ToList();
            }

            var projs = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories);
            var loaded = new List<Project>(projs.Length);
            foreach (var proj in projs)
            {
                var p = await workspace.OpenProjectAsync(proj, cancellationToken: cancellationToken).ConfigureAwait(false);
                loaded.Add(p);
            }

            return loaded;
        }

        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return solution.Projects.ToList();
        }

        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new List<Project>(1) { project };
        }

        throw new ArgumentException($"Unsupported path: {path}. Expected .sln, .csproj, or directory.", nameof(path));
    }

    private static string ResolveRootDirectory(string solutionPath)
    {
        if (Directory.Exists(solutionPath))
        {
            return Path.GetFullPath(solutionPath);
        }

        var full = Path.GetFullPath(solutionPath);
        return Path.GetDirectoryName(full) ?? full;
    }

    private static string ToRelativePath(string root, string? absolute)
    {
        if (string.IsNullOrEmpty(absolute))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetRelativePath(root, absolute).Replace('\\', '/');
        }
        catch
        {
            return absolute.Replace('\\', '/');
        }
    }

    private static bool IsGenerated(SyntaxTree tree)
    {
        var path = tree.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstTrivia = tree.GetRoot().GetLeadingTrivia().ToString();
        return firstTrivia.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamespaceIncluded(INamespaceSymbol? ns, HashSet<string> excludeSet)
    {
        if (ns is null || excludeSet.Count == 0)
        {
            return true;
        }

        var name = ns.ToDisplayString();
        foreach (var prefix in excludeSet)
        {
            if (name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

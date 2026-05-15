using Microsoft.CodeAnalysis;

namespace DistSharp.Roslyn.Internal;

/// <summary>Determines whether a project is a test project based on naming conventions and references.</summary>
internal static class TestProjectDetector
{
    private static readonly string[] TestProjectSuffixes =
    [
        ".Tests",
        ".Test",
        ".UnitTests",
        ".IntegrationTests",
        ".AcceptanceTests",
    ];

    private static readonly string[] TestFrameworkAssemblies =
    [
        "xunit.core",
        "xunit",
        "Microsoft.NET.Test.Sdk",
        "MSTest.TestFramework",
        "nunit.framework",
    ];

    /// <summary>Returns <see langword="true"/> if <paramref name="project"/> appears to be a test project.</summary>
    /// <param name="project">The project to inspect.</param>
    public static bool IsTestProject(Project project)
    {
        foreach (var suffix in TestProjectSuffixes)
        {
            if (project.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var reference in project.MetadataReferences)
        {
            var display = reference.Display;
            if (display is null)
            {
                continue;
            }

            foreach (var asm in TestFrameworkAssemblies)
            {
                if (display.Contains(asm, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

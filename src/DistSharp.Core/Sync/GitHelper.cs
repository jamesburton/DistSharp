using System.Diagnostics;

namespace DistSharp.Core.Sync;

/// <summary>Helpers for reading git metadata from a working tree.</summary>
internal static class GitHelper
{
    /// <summary>
    /// Attempts to resolve the current git HEAD SHA for the repository containing <paramref name="path"/>.
    /// Returns <see langword="null"/> if git is unavailable or the path is not in a git repository.
    /// </summary>
    /// <param name="path">Any path inside the repository (file or directory).</param>
    /// <returns>The full 40-character HEAD SHA, or <see langword="null"/>.</returns>
    public static string? TryGetHeadSha(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        return RunGit(dir, "rev-parse HEAD");
    }

    /// <summary>
    /// Attempts to resolve the current branch name for the repository containing <paramref name="path"/>.
    /// Returns <see langword="null"/> if git is unavailable or the path is not in a git repository.
    /// </summary>
    /// <param name="path">Any path inside the repository (file or directory).</param>
    /// <returns>The abbreviated branch name, or <see langword="null"/>.</returns>
    public static string? TryGetBranch(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        return RunGit(dir, "rev-parse --abbrev-ref HEAD");
    }

    // Runs a git command in workingDir and returns trimmed stdout, or null on failure.
    private static string? RunGit(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();

            return proc.ExitCode == 0 && !string.IsNullOrEmpty(stdout) ? stdout : null;
        }
        catch
        {
            // git not available or not a repo — silent fail per spec §10.
            return null;
        }
    }
}

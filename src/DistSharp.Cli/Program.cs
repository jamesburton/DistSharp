namespace DistSharp.Cli;

/// <summary>Entry point for the DistSharp CLI.</summary>
public static class Program
{
    /// <summary>The main entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static Task<int> Main(string[] args)
    {
        using var host = HostBuilder.Build(args);

        // Commands are wired in Phase 6 Task 3. For now, the host builds and exits 0.
        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine("DistSharp 0.1.0");
        }

        return Task.FromResult(0);
    }
}

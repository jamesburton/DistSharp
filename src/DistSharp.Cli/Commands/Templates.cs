namespace DistSharp.Cli.Commands;

/// <summary>Provides built-in pipeline YAML templates for the <c>init</c> command.</summary>
internal static class Templates
{
    /// <summary>Returns the YAML content for the named template, or <see langword="null"/> if the template name is unknown.</summary>
    /// <param name="templateName">The template identifier (e.g. <c>dotnet-mixed</c>).</param>
    /// <param name="pipelineName">The pipeline name to embed in the template.</param>
    /// <returns>YAML content string, or <see langword="null"/> if the template is not recognised.</returns>
    public static string? Get(string templateName, string pipelineName) => templateName.ToLowerInvariant() switch
    {
        "dotnet-mixed" => Mixed(pipelineName),
        "dotnet-explanation" => Explanation(pipelineName),
        "dotnet-unit-test" => UnitTest(pipelineName),
        "custom" => Custom(pipelineName),
        _ => null,
    };

    private static string Mixed(string name) => $"""
        name: {name}
        version: "1"

        solution:
          path: ./MyApp.sln
          include_tests: false
          include_generated: false
          min_complexity: 3
          exclude_namespaces: []

        steps:
          - name: extract_symbols
            type: RoslynSymbolExtractor

          - name: sample
            type: StratifiedSampler
            depends_on: [extract_symbols]
            config:
              max_rows: 50000
              strategy: complexity_weighted

          - name: generate_explanations
            type: LlmStep
            depends_on: [sample]
            config:
              dataset_type: explanation
              provider: openai
              model: gpt-4.1-mini
              workers: 4
              temperature: 0.7

        output:
          dir: ./training-data
          format: jsonl
          checkpoint_every: 1000
        """;

    private static string Explanation(string name) => $"""
        name: {name}
        version: "1"

        solution:
          path: ./MyApp.sln
          min_complexity: 3

        steps:
          - name: extract_symbols
            type: RoslynSymbolExtractor

          - name: sample
            type: StratifiedSampler
            depends_on: [extract_symbols]
            config:
              max_rows: 10000

          - name: generate
            type: LlmStep
            depends_on: [sample]
            config:
              dataset_type: explanation
              provider: openai
              model: gpt-4.1-mini
              workers: 4

        output:
          dir: ./training-data
          format: jsonl
        """;

    private static string UnitTest(string name) => $"""
        name: {name}
        version: "1"

        solution:
          path: ./MyApp.sln
          min_complexity: 4

        steps:
          - name: extract_symbols
            type: RoslynSymbolExtractor
            config:
              symbol_kinds: [method]

          - name: sample
            type: StratifiedSampler
            depends_on: [extract_symbols]
            config:
              max_rows: 20000

          - name: generate_tests
            type: LlmStep
            depends_on: [sample]
            config:
              dataset_type: unit-test
              provider: openai
              model: gpt-4.1
              workers: 2
              temperature: 0.2

        output:
          dir: ./training-data
          format: jsonl
        """;

    private static string Custom(string name) => $"""
        name: {name}
        version: "1"

        # Customise this template. See README.md for the full configuration reference.

        solution:
          path: ./MyApp.sln

        steps: []

        output:
          dir: ./training-data
          format: jsonl
        """;
}

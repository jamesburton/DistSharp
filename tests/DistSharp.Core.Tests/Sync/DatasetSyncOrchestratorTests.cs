using System.Runtime.CompilerServices;
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace DistSharp.Core.Tests.Sync;

public sealed class DatasetSyncOrchestratorTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public DatasetSyncOrchestratorTests()
    {
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DryRun_ReturnsCorrectCounts_WithoutWritingFiles()
    {
        // Arrange: one symbol in solution, no existing manifest.
        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(new[] { MakeSymbol("A.B.C") }));

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        var plan = await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Drop,
            dryRun: true,
            CancellationToken.None);

        // Assert: no manifest written, plan reflects 1 new symbol
        plan.NewCount.Should().Be(1);
        plan.StaleCount.Should().Be(0);
        plan.UnchangedCount.Should().Be(0);
        plan.OrphanCount.Should().Be(0);
        plan.EstimatedLlmCalls.Should().Be(1);
        File.Exists(Path.Combine(this.tempDir, "_distsharp", "manifest.json")).Should().BeFalse();
        await llm.DidNotReceive().CompleteAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnchangedSymbol_DoesNotCallLlm()
    {
        // Arrange: seed a manifest with a row whose body matches the current symbol.
        var sym = MakeSymbol("A.B.C");
        var bodySha = RowIdentity.ComputeBodySha(sym.BodyText);
        var promptVersion = RowIdentity.DefaultPromptVersion("explanation");
        var rowId = RowIdentity.ComputeRowId(sym.FullyQualifiedName, "explanation", promptVersion);

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = new List<ManifestRow>
            {
                new()
                {
                    Id = rowId,
                    SymbolFqn = sym.FullyQualifiedName,
                    SymbolKind = sym.Kind,
                    DatasetType = "explanation",
                    BodySha = bodySha,
                    PromptVersion = promptVersion,
                    RowOffset = 0,
                },
            },
        };

        await manifest.SaveAsync(Path.Combine(this.tempDir, "_distsharp", "manifest.json"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(this.tempDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, "data", "train.jsonl"),
            "{\"instruction\":\"x\",\"response\":\"y\"}\n",
            CancellationToken.None);

        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(new[] { sym }));

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        var plan = await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Drop,
            dryRun: false,
            CancellationToken.None);

        // Assert: no LLM calls, 1 unchanged
        plan.UnchangedCount.Should().Be(1);
        plan.NewCount.Should().Be(0);
        plan.StaleCount.Should().Be(0);
        await llm.DidNotReceive().CompleteAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleSymbol_TriggersRegeneration()
    {
        // Arrange: manifest body_sha doesn't match current symbol body.
        var sym = MakeSymbol("A.B.C", body: "void Foo() { return 1; }");
        var oldBodySha = RowIdentity.ComputeBodySha("void Foo() { return 0; }"); // different from current
        var promptVersion = RowIdentity.DefaultPromptVersion("explanation");
        var rowId = RowIdentity.ComputeRowId(sym.FullyQualifiedName, "explanation", promptVersion);

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = new List<ManifestRow>
            {
                new()
                {
                    Id = rowId,
                    SymbolFqn = sym.FullyQualifiedName,
                    SymbolKind = sym.Kind,
                    DatasetType = "explanation",
                    BodySha = oldBodySha,
                    PromptVersion = promptVersion,
                    RowOffset = 0,
                },
            },
        };

        await manifest.SaveAsync(Path.Combine(this.tempDir, "_distsharp", "manifest.json"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(this.tempDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, "data", "train.jsonl"),
            "{\"instruction\":\"old\",\"response\":\"old\"}\n",
            CancellationToken.None);

        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(new[] { sym }));
        llm.CompleteAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns("regenerated explanation");

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        var plan = await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Drop,
            dryRun: false,
            CancellationToken.None);

        // Assert: 1 stale row regenerated, 1 LLM call made
        plan.StaleCount.Should().Be(1);
        plan.NewCount.Should().Be(0);
        await llm.Received(1).CompleteAsync(Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>());

        // Manifest body_sha should now match the new symbol
        var newManifest = await DatasetManifest.LoadAsync(
            Path.Combine(this.tempDir, "_distsharp", "manifest.json"),
            CancellationToken.None);
        newManifest!.Rows[0].BodySha.Should().Be(RowIdentity.ComputeBodySha(sym.BodyText));
    }

    [Fact]
    public async Task OrphanPolicy_Drop_RemovesOrphanFromDataset()
    {
        // Arrange: manifest has a row, current solution has NO symbol.
        var orphanRowId = RowIdentity.ComputeRowId("Deleted.Symbol", "explanation", RowIdentity.DefaultPromptVersion("explanation"));

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = new List<ManifestRow>
            {
                new()
                {
                    Id = orphanRowId,
                    SymbolFqn = "Deleted.Symbol",
                    SymbolKind = "method",
                    DatasetType = "explanation",
                    BodySha = "some-sha",
                    PromptVersion = RowIdentity.DefaultPromptVersion("explanation"),
                    RowOffset = 0,
                },
            },
        };

        await manifest.SaveAsync(Path.Combine(this.tempDir, "_distsharp", "manifest.json"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(this.tempDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, "data", "train.jsonl"),
            "{\"instruction\":\"orphan\",\"response\":\"orphan\"}\n",
            CancellationToken.None);

        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();

        // No symbols in solution — everything becomes orphan.
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(Array.Empty<ExtractedSymbol>()));

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        var plan = await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Drop,
            dryRun: false,
            CancellationToken.None);

        // Assert: orphan dropped from manifest and data file
        plan.OrphanCount.Should().Be(1);

        var newManifest = await DatasetManifest.LoadAsync(
            Path.Combine(this.tempDir, "_distsharp", "manifest.json"),
            CancellationToken.None);
        newManifest!.Rows.Should().BeEmpty();

        var lines = await File.ReadAllLinesAsync(Path.Combine(this.tempDir, "data", "train.jsonl"), CancellationToken.None);
        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Should().BeEmpty();
    }

    [Fact]
    public async Task OrphanPolicy_Keep_PreservesOrphanInDataset()
    {
        // Arrange: same setup as Drop test.
        var orphanRowId = RowIdentity.ComputeRowId("Deleted.Symbol", "explanation", RowIdentity.DefaultPromptVersion("explanation"));

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = new List<ManifestRow>
            {
                new()
                {
                    Id = orphanRowId,
                    SymbolFqn = "Deleted.Symbol",
                    SymbolKind = "method",
                    DatasetType = "explanation",
                    BodySha = "some-sha",
                    PromptVersion = RowIdentity.DefaultPromptVersion("explanation"),
                    RowOffset = 0,
                },
            },
        };

        await manifest.SaveAsync(Path.Combine(this.tempDir, "_distsharp", "manifest.json"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(this.tempDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, "data", "train.jsonl"),
            "{\"instruction\":\"orphan\",\"response\":\"keep-me\"}\n",
            CancellationToken.None);

        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(Array.Empty<ExtractedSymbol>()));

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Keep,
            dryRun: false,
            CancellationToken.None);

        // Assert: manifest still has the orphan row, data file still has its content.
        var newManifest = await DatasetManifest.LoadAsync(
            Path.Combine(this.tempDir, "_distsharp", "manifest.json"),
            CancellationToken.None);
        newManifest!.Rows.Should().ContainSingle().Which.SymbolFqn.Should().Be("Deleted.Symbol");

        var dataContent = await File.ReadAllTextAsync(Path.Combine(this.tempDir, "data", "train.jsonl"), CancellationToken.None);
        dataContent.Should().Contain("keep-me");
    }

    [Fact]
    public async Task OrphanPolicy_Archive_MovesOrphanToArchiveDir()
    {
        // Arrange: same setup.
        var orphanRowId = RowIdentity.ComputeRowId("Deleted.Symbol", "explanation", RowIdentity.DefaultPromptVersion("explanation"));

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = new List<ManifestRow>
            {
                new()
                {
                    Id = orphanRowId,
                    SymbolFqn = "Deleted.Symbol",
                    SymbolKind = "method",
                    DatasetType = "explanation",
                    BodySha = "some-sha",
                    PromptVersion = RowIdentity.DefaultPromptVersion("explanation"),
                    RowOffset = 0,
                },
            },
        };

        await manifest.SaveAsync(Path.Combine(this.tempDir, "_distsharp", "manifest.json"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(this.tempDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(this.tempDir, "data", "train.jsonl"),
            "{\"instruction\":\"orphan\",\"response\":\"archive-me\"}\n",
            CancellationToken.None);

        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var llm = Substitute.For<ILlmProvider>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<SolutionAnalysisOptions>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable(Array.Empty<ExtractedSymbol>()));

        var sut = MakeOrchestrator(analyzer, llm);

        // Act
        await sut.SyncAsync(
            this.tempDir,
            "fake.sln",
            "explanation",
            OrphanPolicy.Archive,
            dryRun: false,
            CancellationToken.None);

        // Assert: archive file exists with orphan content; active data file no longer has it.
        var archiveDir = Path.Combine(this.tempDir, "_distsharp", "archive");
        Directory.Exists(archiveDir).Should().BeTrue();
        var archiveFiles = Directory.GetFiles(archiveDir, "*.jsonl");
        archiveFiles.Should().HaveCount(1);
        var archiveContent = await File.ReadAllTextAsync(archiveFiles[0], CancellationToken.None);
        archiveContent.Should().Contain("archive-me");

        var newManifest = await DatasetManifest.LoadAsync(
            Path.Combine(this.tempDir, "_distsharp", "manifest.json"),
            CancellationToken.None);
        newManifest!.Rows.Should().BeEmpty();
    }

    private static DatasetSyncOrchestrator MakeOrchestrator(ISolutionAnalyzer analyzer, ILlmProvider llm)
    {
        return new DatasetSyncOrchestrator(
            analyzer,
            llm,
            NullLogger<DatasetSyncOrchestrator>.Instance);
    }

    private static ExtractedSymbol MakeSymbol(string fqn, string body = "void M() { }")
    {
        return new ExtractedSymbol
        {
            FullyQualifiedName = fqn,
            SignatureText = "void M()",
            BodyText = body,
            ContainingType = "C",
            Namespace = "N",
            FilePath = "x.cs",
            Complexity = 1,
            Kind = "method",
        };
    }

    private static async IAsyncEnumerable<T> AsyncEnumerable<T>(
        IEnumerable<T> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}

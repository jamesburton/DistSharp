using DistSharp.Core.Sync;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Sync;

public sealed class DatasetDiffTests
{
    [Fact]
    public void NoRemoteManifest_AllSymbolsAreNew()
    {
        var current = new[] { Symbol("A.B.C"), Symbol("A.B.D") };
        var result = DatasetDiff.Diff(null, current);

        result.NewRows.Should().HaveCount(2);
        result.StaleRows.Should().BeEmpty();
        result.UnchangedRows.Should().BeEmpty();
        result.OrphanRows.Should().BeEmpty();
    }

    [Fact]
    public void SymbolNotInRemote_IsNew()
    {
        var sym = Symbol("New.Symbol");
        var manifest = new DatasetManifest { Rows = new List<ManifestRow>() };

        var result = DatasetDiff.Diff(manifest, new[] { sym });

        result.NewRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("New.Symbol");
        result.StaleRows.Should().BeEmpty();
        result.UnchangedRows.Should().BeEmpty();
        result.OrphanRows.Should().BeEmpty();
    }

    [Fact]
    public void SymbolInRemoteWithSameBodySha_IsUnchanged()
    {
        var sym = Symbol("A.B.C", bodySha: "matching-sha");
        var manifest = new DatasetManifest
        {
            Rows = new List<ManifestRow> { ManifestRowFor(sym, bodySha: "matching-sha") },
        };

        var result = DatasetDiff.Diff(manifest, new[] { sym });

        result.UnchangedRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("A.B.C");
        result.NewRows.Should().BeEmpty();
        result.StaleRows.Should().BeEmpty();
        result.OrphanRows.Should().BeEmpty();
    }

    [Fact]
    public void SymbolInRemoteWithDifferentBodySha_IsStale()
    {
        var oldSym = Symbol("A.B.C", bodySha: "old-sha");
        var newSym = Symbol("A.B.C", bodySha: "new-sha");

        var manifest = new DatasetManifest
        {
            Rows = new List<ManifestRow> { ManifestRowFor(oldSym, bodySha: "old-sha") },
        };

        var result = DatasetDiff.Diff(manifest, new[] { newSym });

        result.StaleRows.Should().ContainSingle();
        result.StaleRows[0].Existing.SymbolFqn.Should().Be("A.B.C");
        result.StaleRows[0].Existing.BodySha.Should().Be("old-sha");
        result.StaleRows[0].Current.BodySha.Should().Be("new-sha");
        result.NewRows.Should().BeEmpty();
        result.UnchangedRows.Should().BeEmpty();
        result.OrphanRows.Should().BeEmpty();
    }

    [Fact]
    public void RemoteRowWithNoMatchingCurrentSymbol_IsOrphan()
    {
        var deletedSym = Symbol("Deleted.Symbol");
        var manifest = new DatasetManifest
        {
            Rows = new List<ManifestRow> { ManifestRowFor(deletedSym, bodySha: "sha") },
        };

        // No current symbols — everything becomes orphan.
        var result = DatasetDiff.Diff(manifest, Array.Empty<SymbolWithBody>());

        result.OrphanRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("Deleted.Symbol");
        result.NewRows.Should().BeEmpty();
        result.StaleRows.Should().BeEmpty();
        result.UnchangedRows.Should().BeEmpty();
    }

    [Fact]
    public void MultiRow_AllFourCategories_Simultaneously()
    {
        var symNew = Symbol("New.Symbol");
        var symUnchanged = Symbol("Unchanged.Symbol", bodySha: "body-sha");
        var symStaleOld = Symbol("Stale.Symbol", bodySha: "old-body");
        var symStaleNew = Symbol("Stale.Symbol", bodySha: "new-body");
        var symOrphan = Symbol("Orphan.Symbol");

        var manifest = new DatasetManifest
        {
            Rows = new List<ManifestRow>
            {
                ManifestRowFor(symUnchanged, bodySha: "body-sha", offset: 0),
                ManifestRowFor(symStaleOld, bodySha: "old-body", offset: 1),
                ManifestRowFor(symOrphan, bodySha: "orphan-body", offset: 2),
            },
        };

        var current = new[] { symNew, symUnchanged, symStaleNew };
        var result = DatasetDiff.Diff(manifest, current);

        result.NewRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("New.Symbol");
        result.UnchangedRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("Unchanged.Symbol");
        result.StaleRows.Should().ContainSingle().Which.Current.SymbolFqn.Should().Be("Stale.Symbol");
        result.OrphanRows.Should().ContainSingle().Which.SymbolFqn.Should().Be("Orphan.Symbol");
    }

    [Fact]
    public void EmptyManifestAndNoCurrentSymbols_AllEmpty()
    {
        var manifest = new DatasetManifest { Rows = new List<ManifestRow>() };
        var result = DatasetDiff.Diff(manifest, Array.Empty<SymbolWithBody>());

        result.NewRows.Should().BeEmpty();
        result.StaleRows.Should().BeEmpty();
        result.UnchangedRows.Should().BeEmpty();
        result.OrphanRows.Should().BeEmpty();
    }

    private static SymbolWithBody Symbol(string fqn, string datasetType = "explanation", string bodySha = "aaa")
    {
        var promptVersion = RowIdentity.DefaultPromptVersion(datasetType);
        return new SymbolWithBody(fqn, "method", datasetType, promptVersion, bodySha);
    }

    private static ManifestRow ManifestRowFor(SymbolWithBody s, string bodySha, int offset = 0)
    {
        return new ManifestRow
        {
            Id = RowIdentity.ComputeRowId(s.SymbolFqn, s.DatasetType, s.PromptVersion),
            SymbolFqn = s.SymbolFqn,
            SymbolKind = s.SymbolKind,
            DatasetType = s.DatasetType,
            BodySha = bodySha,
            PromptVersion = s.PromptVersion,
            RowOffset = offset,
        };
    }
}

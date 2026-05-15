using DistSharp.Core.Models;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Models;

public sealed class RowTests
{
    [Fact]
    public void Empty_HasNoFields()
    {
        Row.Empty.Fields.Should().BeEmpty();
    }

    [Fact]
    public void With_AddsNewField_ReturnsNewRow()
    {
        var original = Row.Empty;

        var result = original.With("key", "value");

        result.Fields.Should().ContainKey("key").WhoseValue.Should().Be("value");
        original.Fields.Should().BeEmpty(); // original is unchanged
    }

    [Fact]
    public void With_OverwritesExistingField_ReturnsNewRow()
    {
        var row = Row.Empty.With("key", "old");

        var result = row.With("key", "new");

        result.Get<string>("key").Should().Be("new");
        row.Get<string>("key").Should().Be("old"); // original unchanged
    }

    [Fact]
    public void With_Dictionary_MergesAllFields()
    {
        var row = Row.Empty.With("a", 1);

        var result = row.With(new Dictionary<string, object?> { ["b"] = 2, ["c"] = 3 });

        result.Fields.Should().HaveCount(3);
        result.Get<int>("a").Should().Be(1);
        result.Get<int>("b").Should().Be(2);
        result.Get<int>("c").Should().Be(3);
    }

    [Fact]
    public void Get_ReturnsTypedValue_WhenKeyExistsAndTypeMatches()
    {
        var row = Row.Empty.With("count", 42);

        row.Get<int>("count").Should().Be(42);
    }

    [Fact]
    public void Get_ReturnsDefault_WhenKeyMissing()
    {
        Row.Empty.Get<string>("missing").Should().BeNull();
    }

    [Fact]
    public void TryGet_ReturnsTrueAndValue_WhenKeyExistsAndTypeMatches()
    {
        var row = Row.Empty.With("x", 99);

        var found = row.TryGet<int>("x", out var value);

        found.Should().BeTrue();
        value.Should().Be(99);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenKeyMissing()
    {
        var found = Row.Empty.TryGet<string>("missing", out var value);

        found.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void With_WorksOnRowConstructedWithPlainDictionary()
    {
        var row = new Row(new Dictionary<string, object?> { ["a"] = 1 });

        var result = row.With("b", 2);

        result.Get<int>("a").Should().Be(1);
        result.Get<int>("b").Should().Be(2);
    }
}

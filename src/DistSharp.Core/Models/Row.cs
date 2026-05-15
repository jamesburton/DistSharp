using System.Collections.Immutable;

namespace DistSharp.Core.Models;

/// <summary>The fundamental unit of data flowing through a pipeline. Immutable.</summary>
public sealed record Row(IReadOnlyDictionary<string, object?> Fields)
{
    /// <summary>Gets an empty row with no fields.</summary>
    public static Row Empty { get; } = new(ImmutableDictionary<string, object?>.Empty);

    /// <summary>Returns the value of <paramref name="key"/> cast to <typeparamref name="T"/>, or <see langword="default"/> if missing or the wrong type.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The field name.</param>
    /// <returns>The typed value, or <see langword="default"/>.</returns>
    public T? Get<T>(string key)
    {
        if (this.Fields.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        return default;
    }

    /// <summary>Attempts to retrieve the value of <paramref name="key"/> as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The field name.</param>
    /// <param name="value">The typed value if found.</param>
    /// <returns><see langword="true"/> if the key exists and the value is of type <typeparamref name="T"/>.</returns>
    public bool TryGet<T>(string key, out T? value)
    {
        if (this.Fields.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns a new <see cref="Row"/> with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>A new row.</returns>
    public Row With(string key, object? value) =>
        new(((ImmutableDictionary<string, object?>)this.Fields).SetItem(key, value));

    /// <summary>Returns a new <see cref="Row"/> with all entries from <paramref name="fields"/> merged in.</summary>
    /// <param name="fields">Fields to add or overwrite.</param>
    /// <returns>A new row.</returns>
    public Row With(IReadOnlyDictionary<string, object?> fields)
    {
        var builder = (ImmutableDictionary<string, object?>)this.Fields;
        foreach (var kvp in fields)
        {
            builder = builder.SetItem(kvp.Key, kvp.Value);
        }

        return new Row(builder);
    }
}

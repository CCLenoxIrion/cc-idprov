using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Onboarding.Data.Conversion;

/// <summary>JSON serialization for JSON columns and the configuration history.</summary>
public static class JsonColumn
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"JSON column of type {typeof(T).Name} is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Stores a complex object as JSON text.</summary>
public sealed class JsonValueConverter<T>() : ValueConverter<T, string>(
    v => JsonColumn.Serialize(v),
    v => JsonColumn.Deserialize<T>(v));

/// <summary>
/// Compares JSON-mapped objects by content and snapshots them by deep copy, so in-place
/// mutations are detected and original values remain available for the config history.
/// </summary>
public sealed class JsonValueComparer<T>() : ValueComparer<T>(
    (a, b) => JsonColumn.Serialize(a) == JsonColumn.Serialize(b),
    v => JsonColumn.Serialize(v).GetHashCode(StringComparison.Ordinal),
    v => JsonColumn.Deserialize<T>(JsonColumn.Serialize(v)));

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as UTC ticks so SQLite can compare and order it
/// (worker query "NextAttemptAt &lt;= now"). Offsets are normalized to UTC.
/// </summary>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

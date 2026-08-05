using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Telepati.Infrastructure.Data;

/// <summary>
/// SQLite stores no native offset type, so EF falls back to a text encoding that sorts wrong
/// once offsets differ. Persisting UTC ticks keeps <c>ORDER BY CreatedAt</c> correct in dev.
/// </summary>
internal static class SqliteConverters
{
    public static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetConverter =
        new(v => v.ToUniversalTime().Ticks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
}

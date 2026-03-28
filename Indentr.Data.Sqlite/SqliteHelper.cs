using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Indentr.Data.Sqlite;

/// <summary>Extension methods for reading SQLite columns with proper type conversion.</summary>
internal static class SqliteHelper
{
    // SQLite stores UUIDs as lowercase TEXT.
    public static Guid GetGuid(this SqliteDataReader r, int i) =>
        Guid.Parse(r.GetString(i));

    public static Guid? GetNullableGuid(this SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    // SQLite stores timestamps as ISO 8601 roundtrip TEXT (UTC).
    public static DateTime GetDateTime(this SqliteDataReader r, int i) =>
        DateTime.Parse(r.GetString(i), null, DateTimeStyles.RoundtripKind);

    public static DateTime? GetNullableDateTime(this SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), null, DateTimeStyles.RoundtripKind);

    // SQLite stores booleans as INTEGER (0 = false, 1 = true).
    public static bool GetBool(this SqliteDataReader r, int i) =>
        r.GetInt32(i) != 0;

    /// <summary>Current UTC time as an ISO 8601 roundtrip string for use in SQL parameters.</summary>
    public static string UtcNow() => DateTime.UtcNow.ToString("O");

    /// <summary>Formats a DateTime as ISO 8601 roundtrip UTC string.</summary>
    public static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("O");

    /// <summary>Formats a nullable DateTime, returning DBNull for null.</summary>
    public static object IsoOrNull(DateTime? dt) =>
        dt.HasValue ? Iso(dt.Value) : DBNull.Value;

    /// <summary>Creates a SQLiteConnection with foreign keys enabled.</summary>
    public static SqliteConnection Open(string dbPath) =>
        new($"Data Source={dbPath};Foreign Keys=True");
}

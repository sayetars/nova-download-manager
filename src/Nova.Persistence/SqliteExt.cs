using Microsoft.Data.Sqlite;

namespace Nova.Persistence;

/// <summary>Typed parameter helper (Microsoft.Data.Sqlite has no AddWithValue).</summary>
public static class SqliteExt
{
    public static SqliteParameter AddValue(this SqliteParameterCollection col, string name, object? value)
    {
        var p = new SqliteParameter(name, value ?? DBNull.Value);
        col.Add(p);
        return p;
    }
}

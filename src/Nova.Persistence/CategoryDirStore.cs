namespace Nova.Persistence;

/// <summary>
/// v1.8.0 (IDM "Save To" tab parity): category default save directories,
/// persisted in the category_def table. Works for BOTH custom categories and
/// the built-in ones (video, audio, archive, program, document, image, other):
/// a category_def row for a built-in name simply overrides its default dir.
/// Backs the "change folder for … category on last selected" checkbox.
/// </summary>
public sealed class CategoryDirStore
{
    private readonly NovaDatabase _db;
    public CategoryDirStore(NovaDatabase db) => _db = db;

    /// <summary>Current default dir for a category, or null when unset.</summary>
    public string? DirFor(string category)
    {
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT destination_dir FROM category_def WHERE name=@n";
            cmd.Parameters.AddValue("@n", (category ?? "").Trim().ToLowerInvariant());
            var value = cmd.ExecuteScalar() as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>Persists a new default dir (no-op when it already matches).</summary>
    public void SetDir(string category, string dir)
    {
        var name = (category ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(dir)) return;
        if (string.Equals(DirFor(name), dir, StringComparison.Ordinal)) return;
        lock (_db.Connection)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO category_def(name, patterns, destination_dir, icon) VALUES(@n,'',@d,'folder')
                ON CONFLICT(name) DO UPDATE SET destination_dir=@d
                """;
            cmd.Parameters.AddValue("@n", name);
            cmd.Parameters.AddValue("@d", dir);
            cmd.ExecuteNonQuery();
        }
    }
}

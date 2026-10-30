using Nova.Engine;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// DownloadDataFile invariants. T-026 regression: FinalizeAsync must close the
/// write handle BEFORE File.Move — on Windows, renaming a file with an open
/// handle fails with a sharing violation, which made every HTTP download
/// finalize fail and retry forever on real Windows (CI diagnosis round 1:
/// "io_error ... The process cannot access the file ... being used by another
/// process" while received==total). Linux tolerates the open-handle rename,
/// which is why local runs were green.
/// </summary>
public sealed class DownloadDataFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "nova-ddf-test-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) { Directory.CreateDirectory(_dir); return Path.Combine(_dir, name); }

    [Fact]
    public async Task Finalize_Closes_Write_Handle_Before_Move()
    {
        var final = PathFor("file.bin");
        await using var df = DownloadDataFile.OpenOrCreate(final, 8);
        await df.WriteAsync(0, new byte[8], CancellationToken.None);

        var result = await df.FinalizeAsync();

        Assert.Equal(final, result);
        Assert.True(File.Exists(result), "final file must exist");
        Assert.False(File.Exists(df.TempPath), "temp must be gone after the move");
        // The writer must be closed: a write after finalize has to fail instead of
        // silently targeting a disposed/moved handle.
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => df.WriteAsync(0, new byte[1], CancellationToken.None));
    }

    [Fact]
    public async Task Finalize_Move_Tolerates_Share_Delete_Reader()
    {
        // A reader holding the temp with FileShare.Delete (delete/rename-compatible,
        // like an OS preview or a future verifier) must not break finalize.
        // Note: plain FileShare.Read readers DO block renames on Windows — the
        // engine always disposes its verifiers before finalizing, and so does this.
        var final = PathFor("shared.bin");
        await using var df = DownloadDataFile.OpenOrCreate(final, 4);
        await df.WriteAsync(0, new byte[4], CancellationToken.None);
        await using (new FileStream(df.TempPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await df.FinalizeAsync();
            Assert.True(File.Exists(result));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

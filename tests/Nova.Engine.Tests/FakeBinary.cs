using System.Diagnostics;
using Xunit;

namespace Nova.Engine.Tests;

/// <summary>
/// Writes fake executables for the current OS: a POSIX shell script on
/// Linux/macOS, a batch file on Windows. T-026: the .sh variants cannot run on
/// Windows runners ("not a valid application for this OS platform") and
/// <see cref="System.IO.File.SetUnixFileMode"/> throws PlatformNotSupportedException
/// there. Batch notes: parse args with a SHIFT loop (NOT <c>for %%a in (%*)</c> —
/// a ")" inside an argument such as yt-dlp's ".%(ext)s" template closes the FOR
/// set and kills the script); literal percent is "%%" inside .cmd files.
/// </summary>
internal static class FakeBinary
{
    public static async Task<string> WriteAsync(string dir, string name, string shBody, string cmdBody)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            var sh = Path.Combine(dir, name + ".sh");
            await File.WriteAllTextAsync(sh, shBody);
            var chmod = Process.Start("chmod", "+x " + sh);
            Assert.NotNull(chmod);
            Assert.True(chmod!.WaitForExit(5000));
            Assert.Equal(0, chmod.ExitCode);
            return sh;
        }
        var cmd = Path.Combine(dir, name + ".cmd");
        await File.WriteAllTextAsync(cmd, cmdBody);
        return cmd;
    }
}

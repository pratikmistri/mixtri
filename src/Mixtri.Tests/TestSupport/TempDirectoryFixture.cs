namespace Mixtri.Tests.TestSupport;

/// <summary>
/// Shared per-instance temp-directory setup/teardown, replacing the
/// <c>Path.Combine(Path.GetTempPath(), "prefix_" + Guid.NewGuid().ToString("N"))</c> +
/// <c>Directory.CreateDirectory</c>/<c>Directory.Delete</c> blocks copy-pasted across
/// ~14 test files.
/// <para>
/// Per-instance (not static) and Guid-suffixed, so MSTest's method-level parallelism can
/// never collide two tests on the same directory.
/// </para>
/// <para>
/// <see cref="Dispose"/> is non-throwing but does not silently swallow everything: an open
/// file handle (<see cref="IOException"/>) or a permissions race (
/// <see cref="UnauthorizedAccessException"/>) are the expected transient teardown failures
/// and are caught (mirroring the existing narrower catch already used by
/// <c>AudioFileDurationProbeTests</c>); anything else propagates so a real bug is not hidden.
/// </para>
/// </summary>
internal sealed class TempDirectoryFixture : IDisposable
{
    public string Path { get; }

    public TempDirectoryFixture(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { /* a decoder/writer may still hold a file; the temp dir is disposable */ }
        catch (UnauthorizedAccessException) { }
    }
}

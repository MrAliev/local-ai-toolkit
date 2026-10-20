using System.Diagnostics;

namespace LocalAi.Tests.Shared;

/// <summary>Strict cleanup of temporary fixtures after their owned processes have exited.</summary>
internal static class OwnedProcessDirectoryCleanup
{
    internal static void Delete(string directory, Action<Exception, int>? beforeRetry = null,
        int maximumAttempts = 31, Action<TimeSpan>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAttempts, 31);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root, comparison) || target.Length <= root.Length)
            throw new ArgumentException("Only fixture directories strictly below the temporary root can be deleted.", nameof(directory));
        var elapsed = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && (exception is IOException or UnauthorizedAccessException)
                && (exception.HResult & 0xffff) is 5 or 32 or 33
                && attempt < maximumAttempts && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                // File handles may outlive test-owned processes briefly. Never clear
                // attributes, alter ACLs, or suppress an exhausted cleanup failure.
                beforeRetry?.Invoke(exception, attempt);
                var remaining = TimeSpan.FromSeconds(3) - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw;
                var pause = remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);
                if (delay is null) Thread.Sleep(pause);
                else delay(pause);
            }
        }
    }
}

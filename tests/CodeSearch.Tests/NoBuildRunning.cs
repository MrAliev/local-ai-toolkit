using CodeSearch.Mcp;

namespace CodeSearch.Tests;

/// <summary>
/// What a test that is not about the sync line passes for it.
///
/// A notice over a runtime where no build is recorded, which is the ordinary state of a machine
/// and prints nothing at all — so every assertion about what a tool says stays an assertion about
/// that tool. The tests that are about the line build their own notice over a recorded build.
/// </summary>
internal static class NoBuildRunning
{
    internal static SyncNotice Notice => new(_ => null);
}

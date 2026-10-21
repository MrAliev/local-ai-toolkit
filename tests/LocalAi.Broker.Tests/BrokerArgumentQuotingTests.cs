using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using LocalAi.Broker.Client;
using Xunit;

namespace LocalAi.Broker.Tests;

public sealed class BrokerArgumentQuotingTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static IEnumerable<object?[]> ArgumentCases()
    {
        foreach (var root in new[] { @"C:\", @"C:\Local AI\", @"\\server\share\", @"\\?\C:\" })
        {
            yield return [root, null];
            yield return [root, "http://127.0.0.1:11434"];
        }
        yield return [@"C:\LocalAi", ""];
        yield return [@"C:\LocalAi", "  "];
        yield return [@"C:\LocalAi", "http://localhost/a b&c|d;%value%"];
        for (var count = 0; count <= 3; count++)
            yield return [@"C:\LocalAi", "prefix" + new string('\\', count) + "\"middle"];
        for (var count = 1; count <= 3; count++)
            yield return [@"C:\LocalAi", "prefix" + new string('\\', count)];
    }

    [Theory(Skip = "Requires the Windows command-line parser.", SkipUnless = nameof(IsWindows))]
    [MemberData(nameof(ArgumentCases))]
    public void Builder_preserves_each_value_and_the_following_argument(string root, string? url)
    {
        var builder = typeof(BrokerProcess).GetMethod("BuildArguments",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var command = (string)builder.Invoke(null, [root, url])!;
        var expected = new List<string> { "serve", "--runtime", Path.GetFullPath(root) };
        if (!string.IsNullOrWhiteSpace(url)) expected.AddRange(["--ollama", url]);

        // An independent Windows parser checks the actual values and boundaries, not quoting text.
        Assert.Equal(expected, ParseWindowsArguments("\"argv-probe.exe\" " + command).Skip(1));
    }

    private static string[] ParseWindowsArguments(string command)
    {
        var memory = CommandLineToArgvW(command, out var count);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(
                Marshal.ReadIntPtr(memory, index * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(memory); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

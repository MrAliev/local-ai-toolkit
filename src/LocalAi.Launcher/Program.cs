using LocalAi.Contracts;
using LocalAi.Launcher;

// Before anything is printed or relayed. This process writes almost nothing of its own: what
// reaches the console is a child's raw bytes, copied stream to stream by ToolRunner. Those bytes
// are already UTF-8 — the child says so at the top of its own Main — but the console they land in
// is still on its own code page, and on a Russian machine that is 866. The command succeeded and
// the reader saw mojibake.
//
// Only the console is told; the copying is untouched. Where there is no console to tell — a Git
// hook, an MCP server reading the pipe — this returns false and the bytes were never in question.
ConsoleOutputText.UseUtf8();

var launcherPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("The launcher executable path is unavailable.");
var launcherDirectory = AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar,
    Path.AltDirectorySeparatorChar);
var binRoot = Directory.GetParent(launcherDirectory)?.FullName
    ?? throw new InvalidOperationException("The launcher bin root is unavailable.");

return await LauncherProgram.RunAsync(
    args,
    binRoot,
    launcherPath,
    Console.Error,
    CancellationToken.None);

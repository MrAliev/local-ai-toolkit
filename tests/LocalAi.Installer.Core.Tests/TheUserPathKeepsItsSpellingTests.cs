using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.TestFixtures;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The account's PATH is somebody else's value with one entry of ours in it. It used to be read
/// expanded and written back as REG_SZ, so a REG_EXPAND_SZ value holding %USERPROFILE% came back
/// as a plain string with the profile spelled out in every entry, Windows' own included, and
/// removal left it that way.
///
/// Everything here works on a key of its own under HKCU, never the real Environment key.
/// </summary>
public sealed class TheUserPathKeepsItsSpellingTests : IDisposable
{
    private const string Launcher = @"C:\Users\x\AppData\Local\LocalAi\bin\launcher";

    private const string Seeded =
        @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;%USERPROFILE%\seed-tools;C:\Seed Tools\bin";

    private readonly string root = @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N");

    private readonly string journals = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.PathJournals",
        Guid.NewGuid().ToString("N"));

    private readonly List<string?> announcements = [];

    private string SubKey => root + @"\Environment";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            if (Directory.Exists(journals)) Directory.Delete(journals, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [Theory]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.String)]
    public void Installing_appends_our_entry_and_keeps_the_rest_as_written(RegistryValueKind kind)
    {
        Seed(Seeded, kind);

        Assert.True(LauncherPathRegistration.Register(Launcher, Store()));

        Assert.Equal(Seeded + ";" + Launcher, Raw());
        Assert.Equal(kind, Kind());
    }

    [Fact]
    public void Removal_gives_back_the_value_installation_found()
    {
        Seed(Seeded, RegistryValueKind.ExpandString);
        LauncherPathRegistration.Register(Launcher, Store());

        Assert.True(LauncherPathRegistration.Unregister(Launcher, Store()));

        Assert.Equal(Seeded, Raw());
        Assert.Equal(RegistryValueKind.ExpandString, Kind());
    }

    /// <summary>Told after the value is written, once per change, and not when nothing changed.</summary>
    [Fact]
    public void Other_processes_are_told_of_each_change_and_only_of_a_change()
    {
        Seed(Seeded, RegistryValueKind.ExpandString);

        LauncherPathRegistration.Register(Launcher, Store());
        LauncherPathRegistration.Register(Launcher, Store());
        LauncherPathRegistration.Unregister(Launcher, Store());
        LauncherPathRegistration.Unregister(Launcher, Store());

        Assert.Equal([Seeded + ";" + Launcher, Seeded], announcements);
    }

    /// <summary>Empty entries somebody else left are theirs; only the slot ours took goes.</summary>
    [Fact]
    public void Removal_leaves_the_empty_entries_it_did_not_add()
    {
        var before = @"C:\A;;C:\B;" + Launcher + ";";

        Assert.Equal(@"C:\A;;C:\B;", LauncherPathRegistration.Remove(before, Launcher));
    }

    /// <summary>
    /// The value is read as written now, so an entry naming our directory through a variable
    /// is recognised by what it expands to: not added a second time, and taken off on removal.
    /// </summary>
    [Fact]
    public void An_entry_spelled_with_a_variable_is_still_ours()
    {
        var directory = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE")!,
            "LocalAi-launcher");
        var path = @"C:\A;%USERPROFILE%\LocalAi-launcher";

        Assert.True(LauncherPathRegistration.Contains(path, directory));
        Assert.Equal(path, LauncherPathRegistration.Add(path, directory));
        Assert.Equal(@"C:\A", LauncherPathRegistration.Remove(path, directory));
    }

    /// <summary>
    /// The write is followed by a broadcast every top-level window has to answer, and the thread
    /// that makes it answers nothing meanwhile — the installer's window, when that thread is its.
    /// </summary>
    [Fact]
    public async Task Installing_writes_the_path_off_the_thread_that_asked()
    {
        var store = new RecordingStore(@"C:\Windows");

        var window = await WindowThread.RunAsync(
            () => LauncherPathRegistration.RegisterAsync(Launcher, store));

        Assert.True(window.Result);
        Assert.NotNull(store.WriterThread);
        Assert.NotEqual(window.ThreadId, store.WriterThread);
    }

    [Fact]
    public async Task A_removal_writes_the_path_off_the_thread_that_runs_it()
    {
        using var machine = new RemovalFixture();
        var store = new RecordingStore(@"C:\Windows;" + Path.Combine(machine.Layout.BinRoot, "launcher"));
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Token,
            readUserPath: store.Read);
        Assert.True(plan.RemovesLauncherFromPath);

        var window = await WindowThread.RunAsync(() => Apply(machine, plan, store));

        Assert.True(window.Result.Succeeded);
        Assert.Equal(@"C:\Windows", store.Value);
        Assert.NotNull(store.WriterThread);
        Assert.NotEqual(window.ThreadId, store.WriterThread);
    }

    /// <summary>The value a clean Windows account has: one variable entry and a separator at the end.</summary>
    [Fact]
    public async Task A_value_ending_in_a_separator_gains_no_empty_entry_and_comes_back_whole()
    {
        const string windowsDefault = @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;";
        using var machine = new RemovalFixture();
        var launcher = Path.Combine(machine.Layout.BinRoot, "launcher");
        Seed(windowsDefault, RegistryValueKind.ExpandString);
        var store = Store();

        Assert.True(LauncherPathRegistration.Register(launcher, store));
        Assert.Equal(windowsDefault + launcher + ";", Raw());

        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Token,
            readUserPath: store.Read);
        Assert.True(plan.RemovesLauncherFromPath);
        var outcome = await Apply(machine, plan, store);

        Assert.True(outcome.Succeeded);
        Assert.Equal(windowsDefault, Raw());
        Assert.Equal(RegistryValueKind.ExpandString, Kind());
    }

    [Fact]
    public async Task A_full_uninstall_gives_back_the_path_installation_found()
    {
        using var machine = new RemovalFixture();
        Seed(Seeded + ";" + Path.Combine(machine.Layout.BinRoot, "launcher"), RegistryValueKind.ExpandString);
        var store = Store();
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Token,
            readUserPath: store.Read);
        Assert.True(plan.RemovesLauncherFromPath);

        var outcome = await Apply(machine, plan, store);

        Assert.True(outcome.Succeeded);
        Assert.Equal(Seeded, Raw());
        Assert.Equal(RegistryValueKind.ExpandString, Kind());
    }

    /// <summary>
    /// A PATH this account may not write is one more thing the removal reports, as it was while
    /// the write went through the runtime: the run finishes and the value stays as it was.
    /// </summary>
    [Fact]
    public async Task A_removal_that_may_not_write_the_path_reports_it_and_finishes()
    {
        using var machine = new RemovalFixture();
        var launcher = Path.Combine(machine.Layout.BinRoot, "launcher");
        Seed(Seeded + ";" + launcher, RegistryValueKind.ExpandString);
        var store = Store();
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Token,
            readUserPath: store.Read);
        Assert.True(plan.RemovesLauncherFromPath);
        DenyWritingValues();

        var outcome = await Apply(machine, plan, store);

        Assert.Equal(launcher, Assert.Single(outcome.Failures).Path);
        Assert.Equal(Seeded + ";" + launcher, Raw());
    }

    private void DenyWritingValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            SubKey,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var security = key.GetAccessControl();
        security.AddAccessRule(new RegistryAccessRule(
            WindowsIdentity.GetCurrent().User!,
            RegistryRights.SetValue,
            AccessControlType.Deny));
        key.SetAccessControl(security);
    }

    private async Task<UninstallOutcome> Apply(
        RemovalFixture machine,
        UninstallPlan plan,
        IUserPathStore store)
    {
        using var journal = InstallerRunJournal.Start(journals);
        return await new UninstallRunner(
                machine.Layout,
                new LauncherThatStops(),
                registrySubKey: RemovalFixture.UnusedRegistrySubKey,
                userPath: store)
            .ApplyAsync(plan, journal, Token);
    }

    private WindowsUserPathStore Store() => new(SubKey, () => announcements.Add(Raw()));

    private void Seed(string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        key.SetValue("Path", value, kind);
    }

    private string? Raw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private RegistryValueKind Kind()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey)!;
        return key.GetValueKind("Path");
    }

    private sealed class RecordingStore(string? value) : IUserPathStore
    {
        public string? Value { get; private set; } = value;

        public int? WriterThread { get; private set; }

        public string? Read() => Value;

        public void Write(string value)
        {
            WriterThread = Environment.CurrentManagedThreadId;
            Value = value;
        }
    }

    private sealed class LauncherThatStops : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false, false));
    }

    /// <summary>
    /// A thread that runs whatever is posted to it, as a window's thread does: the code under
    /// test resumes here after every await, so what it does synchronously is done here.
    /// </summary>
    private sealed class WindowThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Posted after the body finished: nothing is left to run it.
            }
        }

        public static async Task<(T Result, int ThreadId)> RunAsync<T>(Func<Task<T>> body)
        {
            var context = new WindowThread();
            var finished = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threadId = 0;
            var thread = new Thread(() =>
            {
                threadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(context);
                context.Post(
                    async _ =>
                    {
                        try
                        {
                            finished.SetResult(await body());
                        }
                        catch (Exception exception)
                        {
                            finished.SetException(exception);
                        }
                        finally
                        {
                            context.queue.CompleteAdding();
                        }
                    },
                    null);
                foreach (var (callback, state) in context.queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            });
            thread.Start();
            var result = await finished.Task;
            thread.Join();
            return (result, threadId);
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using Ntilde.Pty;

namespace Ntilde.Tests;

[Collection(PtyRealShellCollection.Name)]
[Trait("Category", "PtySmoke")]
public sealed class PtyNativeChildWaitObservationTests
{
    private static void RequireUnix() => Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
        "The genuine Unix wait fact is unavailable on Windows; Windows desktop qualification is deferred.");
    private static RustPtySession Session(RustPtySession.PtyReadDelegate? read = null,
        RustPtySession.PtyTryGetExitCodeDelegate? probe = null, string args = "-c \"exec /bin/sleep 30\"")
        => new("/bin/sh", 80, 24, args, null, true,
            new Dictionary<string, string> { ["ENV"] = "", ["BASH_ENV"] = "", ["PS1"] = "", ["PS2"] = "", ["PROMPT_COMMAND"] = "" }, read, probe);

    [Fact]
    public void Repeated_passive_getter_never_calls_the_native_probe_on_its_caller()
    {
        RequireUnix(); int caller = 0, callerProbes = 0;
        using var session = Session(probe: (RustPtySession.PtySafeHandle _, out int code) =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref caller)) Interlocked.Increment(ref callerProbes);
            code = 123; return 0;
        });
        int pid = Assert.IsType<int>(session.Pid);
        Volatile.Write(ref caller, Environment.CurrentManagedThreadId);
        for (int i = 0; i < 1000; i++)
        {
            var fact = Assert.IsType<NativeChildWaitObservation>(session.ReadNativeChildWaitObservation());
            Assert.Equal(session.Id, fact.NativeSessionId); Assert.Equal(pid, fact.OriginalChildPid);
            Assert.False(fact.NativeWaitObserved); Assert.Null(fact.ExitCode);
        }
        Assert.Equal(0, callerProbes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Actual_native_own_child_wait_preserves_original_PID_generation_and_real_code(int code)
    {
        RequireUnix(); using var session = Session(args: "-c \"exit " + code + "\"");
        var initial = Assert.IsType<NativeChildWaitObservation>(session.ReadNativeChildWaitObservation());
        var observed = await WaitForAsync(session, value => value.NativeWaitObserved);
        Assert.Equal(session.Id, observed.NativeSessionId); Assert.Equal(initial.OriginalChildPid, observed.OriginalChildPid);
        Assert.Equal(code, observed.ExitCode); Assert.True(observed.NativeWaitObserved);
        Assert.Equal(observed, session.ReadNativeChildWaitObservation());
        var handle = (RustPtySession.PtySafeHandle)typeof(RustPtySession).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        handle.Dispose(); // The genuine historical wait remains cached without native I/O.
        Assert.Equal(observed, session.ReadNativeChildWaitObservation());
        session.Dispose(); Assert.Null(session.ReadNativeChildWaitObservation());
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("missingExport")]
    [InlineData("disposedProbe")]
    public async Task EOF_and_generic_fallback_zero_never_manufacture_a_native_wait(string kind)
    {
        RequireUnix();
        using var session = Session(read: (_, _, _) => 0, probe: (RustPtySession.PtySafeHandle _, out int code) =>
        {
            code = 0;
            if (kind == "missingExport") throw new EntryPointNotFoundException("Controlled missing native wait export.");
            ObjectDisposedException.ThrowIf(kind == "disposedProbe", "controlled probe");
            return -1;
        });
        var deadline = Stopwatch.StartNew();
        while (!session.ExitCode.HasValue && deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            // Short on purpose: observe existing notification fallback without invoking the native getter.
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Equal(0, session.ExitCode);
        var fact = Assert.IsType<NativeChildWaitObservation>(session.ReadNativeChildWaitObservation());
        Assert.False(fact.NativeWaitObserved); Assert.Null(fact.ExitCode);
        Assert.Equal(session.Id, fact.NativeSessionId);
    }

    [Fact]
    public void Closed_native_handle_does_not_replace_original_PID_or_create_a_wait_fact()
    {
        RequireUnix(); using var session = Session(read: (_, _, _) => 0,
            probe: (RustPtySession.PtySafeHandle _, out int code) => { code = 0; return 0; });
        int pid = Assert.IsType<int>(session.Pid); var generation = session.Id;
        var handle = (RustPtySession.PtySafeHandle)typeof(RustPtySession).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        handle.Dispose(); Assert.True(handle.IsClosed); Assert.Null(session.Pid);
        var fact = Assert.IsType<NativeChildWaitObservation>(session.ReadNativeChildWaitObservation());
        Assert.Equal(pid, fact.OriginalChildPid); Assert.Equal(generation, fact.NativeSessionId);
        Assert.False(fact.NativeWaitObserved); Assert.Null(fact.ExitCode);
        session.Dispose(); Assert.Null(session.ReadNativeChildWaitObservation());
    }

    [Fact]
    public async Task Concurrent_passive_reads_never_publish_true_with_an_unobserved_code()
    {
        RequireUnix(); using var session = Session(args: "-c \"exit 17\"");
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var fact = await WaitForAsync(session, value =>
            {
                Assert.Equal(value.NativeWaitObserved, value.ExitCode.HasValue);
                if (value.NativeWaitObserved) Assert.Equal(17, value.ExitCode);
                return value.NativeWaitObserved;
            });
            Assert.Equal(session.Id, fact.NativeSessionId);
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(readers);
    }

    private static async Task<NativeChildWaitObservation> WaitForAsync(RustPtySession session, Func<NativeChildWaitObservation, bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            var fact = Assert.IsType<NativeChildWaitObservation>(session.ReadNativeChildWaitObservation());
            if (predicate(fact)) return fact;
            // Short on purpose: poll the passive cache, never initiate a native wait from this path.
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Fail("The existing native watcher did not publish the expected cached wait fact.");
        return default;
    }
}

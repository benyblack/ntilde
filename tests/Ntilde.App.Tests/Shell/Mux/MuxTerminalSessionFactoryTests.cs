using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxTerminalSessionFactoryTests
{
    private static TerminalSessionRequest Local(Guid? existing = null) =>
        new("scripted", "", "", 80, 24, null, false, null, existing);

    private static (MuxTestHost Mux, MuxTerminalSessionFactory Factory, RecordingSessionFactory Fallback) Build()
    {
        var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test-endpoint", null);
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        return (mux, new MuxTerminalSessionFactory(host, fallback, null), fallback);
    }

    [Fact]
    public void Local_request_spawns_an_unattached_mux_session()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local());
            Assert.Equal(PersistentSessionOutcome.Spawned, r.Outcome);
            var session = Assert.IsType<MuxClientSession>(r.Session);
            Assert.False(session.IsAttached);
            Assert.Contains(session.Id, mux.Server.GetSessionIds());
            Assert.Equal("test-endpoint", r.Endpoint);
        }
    }

    [Fact]
    public void Ssh_request_goes_to_the_fallback()
    {
        var (mux, factory, fallback) = Build();
        using (mux) using (factory.Host)
        {
            var ssh = new TerminalSessionRequest("", "", "", 80, 24, null, false, new SshSessionDescriptor(Guid.NewGuid(), 0, null, false));
            PersistentSessionResult r = factory.CreatePersistent(ssh);
            Assert.Equal(PersistentSessionOutcome.NotPersistent, r.Outcome);
            Assert.Same(ssh, fallback.LastRequest);
            Assert.Empty(mux.Server.GetSessionIds());
        }
    }

    [Fact]
    public void Existing_running_id_reattaches_without_spawning()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            var first = (MuxClientSession)factory.CreatePersistent(Local()).Session;
            Guid id = first.Id;
            first.Dispose(); // detach, as a pane does on Reconnect
            PersistentSessionResult r = factory.CreatePersistent(Local(id));
            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            Assert.Equal(id, ((MuxClientSession)r.Session).Id);
            Assert.Single(mux.Server.GetSessionIds());
        }
    }

    [Fact]
    public void Missing_id_spawns_fresh_and_reports_PreviousLost()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local(Guid.NewGuid()));
            Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
            Assert.IsType<MuxClientSession>(r.Session);
        }
    }

    [Fact]
    public void Unreachable_daemon_falls_back_to_a_local_session_and_says_so()
    {
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        using var host = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "x", null);
        var factory = new MuxTerminalSessionFactory(host, fallback, null) { ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        PersistentSessionResult r = factory.CreatePersistent(Local());
        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.NotNull(r.Detail);
    }

    [Fact]
    public void Spawn_failure_on_the_daemon_falls_back()
    {
        var (mux, factory, fallback) = Build();
        using (mux) using (factory.Host)
        {
            mux.Factory.ThrowOnCreate = true; // add to ScriptedSessionFactory if absent
            PersistentSessionResult r = factory.CreatePersistent(Local());
            Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
            Assert.NotNull(fallback.LastRequest);
        }
    }
}

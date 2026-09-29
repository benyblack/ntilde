using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxTerminalSessionFactoryTests
{
    private static TerminalSessionRequest Local(Guid? existing = null) =>
        new("scripted", "", "", 80, 24, null, false, null, existing);

    private static (MuxTestHost Mux, MuxTerminalSessionFactory Factory, RecordingSessionFactory Fallback) Build(MuxServerOptions? serverOptions = null)
    {
        var mux = new MuxTestHost(serverOptions);
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test-endpoint", null);
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        return (mux, new MuxTerminalSessionFactory(host, fallback, null), fallback);
    }

    private static ClientPaneModel AttachOtherClient(MuxTestHost mux) => Task.Run(async () =>
    {
        MuxClient c = await mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return await MuxTestHost.AttachPaneAsync(c, id);
    }).GetAwaiter().GetResult();

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

    /// <summary>
    /// PR #489 review 2, item 4: a pane reopening a daemon session whose daemon cannot be reached
    /// gets no session at all - not a stand-in local shell that would bury the running one and
    /// lose its id. The plain Create contract, which cannot say "none", still falls back.
    /// </summary>
    [Fact]
    public void Unreachable_daemon_with_an_existing_id_starts_nothing_and_says_DaemonUnreachable()
    {
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        using var host = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "x", null);
        var factory = new MuxTerminalSessionFactory(host, fallback, null) { ConnectTimeout = TimeSpan.FromMilliseconds(500) };

        PersistentSessionResult r = factory.CreatePersistent(Local(Guid.NewGuid()));

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(fallback.LastRequest);
        Assert.NotNull(r.Detail);
        Assert.IsType<FakeTerminalSession>(factory.Create(Local(Guid.NewGuid())));
    }

    /// <summary>
    /// Against a v1 daemon the factory keeps Phase 2's client-side check: a session another client is
    /// attached to stays with it, and this instance gets a fresh shell - now reported as AttachedElsewhere.
    /// </summary>
    [Fact]
    public void A_session_attached_by_another_client_is_not_taken_over()
    {
        var (mux, factory, _) = Build(new MuxServerOptions { MaxProtocolVersion = 1, ForceConPtyFiltering = false });
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs));

            Assert.Equal(PersistentSessionOutcome.AttachedElsewhere, r.Outcome);
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.NotEqual(theirs, mine.Id);
            Assert.Contains(theirs, mux.Server.GetSessionIds());
            Assert.Contains(mine.Id, mux.Server.GetSessionIds());
            Assert.True(other.Session.IsAttached, "the other client's session is untouched");
            Assert.False(mux.Fake(theirs).Disposed);
        }
    }

    [Fact]
    public void On_v2_a_restore_opens_IfUnattached_and_the_attach_itself_refuses()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs));

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);   // no client-side count check on v2
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.Equal(theirs, mine.Id);
            Assert.Equal(MuxAttachMode.IfUnattached, mine.AttachMode);
            var ex = Assert.Throws<MuxProtocolException>(() =>
                Task.Run(() => mine.AttachAsync(0, MuxTestHost.DefaultPresentation)).GetAwaiter().GetResult());
            Assert.Equal(MuxErrorCodes.SessionAttached, ex.Code);
            Assert.True(other.Session.IsAttached);
        }
    }

    [Fact]
    public void AttachShared_joins_a_session_another_client_holds()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.Equal((theirs, MuxAttachMode.Shared), (mine.Id, mine.AttachMode));
            Task.Run(() => mine.AttachAsync(0, MuxTestHost.DefaultPresentation)).GetAwaiter().GetResult();
            TestWait.UntilAsync(() => mux.Mux(theirs).AttachedClients == 2, "both attached").GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void AttachShared_to_an_exited_session_still_attaches()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);   // keeps the exited session from being reaped
            Guid theirs = other.Session.Id;
            mux.Fake(theirs).Exit(4);
            TestWait.UntilAsync(() => mux.Mux(theirs).IsExited, "the mux saw the exit").GetAwaiter().GetResult();

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            Assert.Equal(theirs, Assert.IsType<MuxClientSession>(r.Session).Id);
        }
    }

    [Fact]
    public void AttachShared_to_a_missing_session_spawns_fresh_and_says_the_previous_one_is_lost()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local(Guid.NewGuid()) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
            Assert.Contains(Assert.IsType<MuxClientSession>(r.Session).Id, mux.Server.GetSessionIds());
        }
    }

    /// <summary>Final-fix item 4: a faulted session named by the restore is ended, not leaked, before the fresh spawn.</summary>
    [Fact]
    public void A_faulted_existing_session_is_killed_before_a_fresh_one_is_spawned()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            Guid old = Task.Run(async () =>
            {
                MuxClient c = await mux.ConnectClientAsync();
                return await MuxTestHost.SpawnAsync(c);
            }).GetAwaiter().GetResult();
            ScriptedTerminalSession child = mux.Fake(old);
            Task.Run(() => mux.Mux(old).MakeParserThrowOnReplyAsync()).GetAwaiter().GetResult();
            child.Emit("\x1b[c"); // DA1: the reply throws inside the daemon's parser, which faults the session
            TestWait.UntilAsync(() => mux.Mux(old).IsFaulted, "the session faulted", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

            PersistentSessionResult r = factory.CreatePersistent(Local(old));

            Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
            Assert.NotEqual(old, Assert.IsType<MuxClientSession>(r.Session).Id);
            TestWait.UntilAsync(() => child.Disposed, "the faulted session's child was killed", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    /// <summary>Final-fix item 10: a daemon of another protocol version is reported as such, with the kill-server hint.</summary>
    [Fact]
    public void A_version_mismatch_falls_back_and_says_so()
    {
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        using var host = new MuxConnectionHost(
            _ => throw new MuxUnavailableException($"different version. {MuxDaemonLauncher.KillServerHint}", versionMismatch: true), "x", null);
        var factory = new MuxTerminalSessionFactory(host, fallback, null) { ConnectTimeout = TimeSpan.FromSeconds(2) };

        PersistentSessionResult r = factory.CreatePersistent(Local());

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.True(r.VersionMismatch);
        Assert.Contains("kill-server", r.Detail);
        Assert.IsType<FakeTerminalSession>(r.Session);
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

    /// <summary>PR #489 follow-up: a saved id whose daemon session runs another program is not reattached.</summary>
    [Fact]
    public void A_restore_whose_command_differs_spawns_fresh_and_leaves_the_session_alone()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            Guid theirs = Task.Run(async () =>
            {
                MuxClient c = await mux.ConnectClientAsync();
                return await MuxTestHost.SpawnAsync(c);           // Command = "scripted"
            }).GetAwaiter().GetResult();

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { Command = "other-shell" });

            Assert.Equal(PersistentSessionOutcome.Spawned, r.Outcome);
            Assert.NotEqual(theirs, Assert.IsType<MuxClientSession>(r.Session).Id);
            Assert.Contains(theirs, mux.Server.GetSessionIds());
            Assert.False(mux.Fake(theirs).Disposed);
        }
    }
}

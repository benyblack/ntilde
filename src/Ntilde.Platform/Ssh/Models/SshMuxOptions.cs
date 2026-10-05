namespace Ntilde.Platform.Ssh.Models;

public sealed class SshMuxOptions
{
    public bool Enabled { get; set; }
    public bool ControlMasterAuto { get; set; } = true;
    public string ControlPath { get; set; } = string.Empty;
    public int ControlPersistSeconds { get; set; }

    /// <summary>Phase 4: tabs of this profile run in ntilde-mux on the remote host and survive disconnects.</summary>
    public bool PersistRemoteSessions { get; set; }

    /// <summary>Absolute remote path recorded by the install flow; empty = the default under $HOME.</summary>
    public string RemoteDaemonPath { get; set; } = string.Empty;

    public string RemoteDaemonVersion { get; set; } = string.Empty;

    public string RemoteDaemonRid { get; set; } = string.Empty;
}

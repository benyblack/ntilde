using Ntilde.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Replay;

namespace Ntilde.UI.Replay
{
    public class ReplayViewModel : INotifyPropertyChanged
    {
        private readonly string _filePath;
        private long _currentTimeMs;
        private long _durationMs;
        private bool _isPlaying;
        private double _playbackSpeed = 1.0;
        private string _timeDisplay = "00:00 / 00:00";

        private List<SnapshotIndexEntry> _snapshots = new();
        private ReplayHeader? _header;

        public event PropertyChangedEventHandler? PropertyChanged;

        public long CurrentTimeMs
        {
            get => _currentTimeMs;
            set { _currentTimeMs = value; OnPropertyChanged(); UpdateTimeDisplay(); }
        }

        public long DurationMs
        {
            get => _durationMs;
            set { _durationMs = value; OnPropertyChanged(); UpdateTimeDisplay(); }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { _isPlaying = value; OnPropertyChanged(); }
        }

        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set { _playbackSpeed = value; OnPropertyChanged(); }
        }

        public string TimeDisplay
        {
            get => _timeDisplay;
            set { _timeDisplay = value; OnPropertyChanged(); }
        }

        public ReplayViewModel(string filePath)
        {
            _filePath = filePath;
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            // Scan file for duration and snapshots
            try
            {
                using var reader = new StreamReader(_filePath);
                string? line = await reader.ReadLineAsync();
                if (line == null) return;

                _header = System.Text.Json.JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayHeader);

                long lastTime = 0;
                long lineNumber = 1;
                var snapshots = new List<SnapshotIndexEntry>();
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var ev = System.Text.Json.JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent);
                        if (ev != null)
                        {
                            lastTime = ev.TimeOffsetMs;
                            if (ev.Type == "snapshot" && ev.Snapshot != null)
                            {
                                snapshots.Add(new SnapshotIndexEntry { TimeMs = ev.TimeOffsetMs, Snapshot = ev.Snapshot, LineNumber = lineNumber });
                            }
                        }
                    }
                    catch { /* skip malformed lines */ }
                }

                // Publish once: playback can read the index without racing the scan's list edits.
                _snapshots = snapshots;
                DurationMs = lastTime;
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[ReplayViewModel] Init Error: {ex.Message}");
            }
        }

        private void UpdateTimeDisplay()
        {
            TimeDisplay = $"{FormatTime(CurrentTimeMs)} / {FormatTime(DurationMs)}";
        }

        private static string FormatTime(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public class SnapshotIndexEntry
        {
            public long TimeMs { get; set; }
            public ReplaySnapshot Snapshot { get; set; } = null!;
            public long LineNumber { get; set; }
        }

        public ReplaySnapshot? GetNearestSnapshot(long targetTimeMs)
        {
            return GetNearestSnapshotEntry(targetTimeMs)?.Snapshot;
        }

        public SnapshotIndexEntry? GetNearestSnapshotEntry(long targetTimeMs)
        {
            return _snapshots.OrderByDescending(s => s.TimeMs)
                             .ThenByDescending(s => s.LineNumber)
                             .FirstOrDefault(s => s.TimeMs <= targetTimeMs);
        }
        /// <summary>Select a claimed complete checkpoint; restoration validates it before mutation.</summary>
        public SnapshotIndexEntry? GetNearestSeekCheckpoint(long targetTimeMs) =>
            _snapshots.Where(s => s.TimeMs <= targetTimeMs && s.Snapshot.State is { Version: TerminalStateSnapshot.CurrentVersion })
                      .OrderByDescending(s => s.TimeMs)
                      .ThenByDescending(s => s.LineNumber)
                      .FirstOrDefault();
    }
}

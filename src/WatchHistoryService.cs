using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace LiteTube
{
    public class WatchHistoryEntry
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public long DurationSeconds { get; set; }
        public long LastPositionSeconds { get; set; }
        public DateTime WatchedAtUtc { get; set; }
    }

    /// <summary>
    /// Serviço leve de continuidade local e histórico de reprodução em data/watch_history.json.
    /// Utiliza exclusivamente System.Text.Json, mantém até 100 registros ordenados por último acesso
    /// e gerencia a retomada automática (Auto-Resume) sem dependência de conta Google.
    /// </summary>
    public sealed class WatchHistoryService
    {
        private const int MaxHistoryEntries = 100;
        private const long MinDurationForResumeSeconds = 300; // 5 minutos

        private static readonly Lazy<WatchHistoryService> _lazyInstance =
            new Lazy<WatchHistoryService>(() => new WatchHistoryService());

        public static WatchHistoryService Instance => _lazyInstance.Value;

        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly object _fileIoLock = new object();
        private readonly List<WatchHistoryEntry> _entries = new List<WatchHistoryEntry>();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private WatchHistoryService()
        {
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            _filePath = Path.Combine(dataDir, "watch_history.json");
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            lock (_lock)
            {
                _entries.Clear();
                try
                {
                    if (!File.Exists(_filePath)) return;
                    string json = File.ReadAllText(_filePath);
                    if (string.IsNullOrWhiteSpace(json)) return;

                    var loaded = JsonSerializer.Deserialize<List<WatchHistoryEntry>>(json, JsonOptions);
                    if (loaded != null && loaded.Count > 0)
                    {
                        loaded.Sort((a, b) => b.WatchedAtUtc.CompareTo(a.WatchedAtUtc));
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in loaded)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.VideoId)) continue;
                            if (!seen.Add(item.VideoId)) continue;
                            _entries.Add(item);
                            if (_entries.Count >= MaxHistoryEntries) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("HISTORY", "Falha ao carregar watch_history.json: " + ex.Message);
                }
            }
        }

        private List<WatchHistoryEntry> CloneSnapshotUnsafe()
        {
            var copy = new List<WatchHistoryEntry>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                copy.Add(new WatchHistoryEntry
                {
                    VideoId = e.VideoId,
                    Title = e.Title,
                    Author = e.Author,
                    ThumbnailUrl = e.ThumbnailUrl,
                    DurationSeconds = e.DurationSeconds,
                    LastPositionSeconds = e.LastPositionSeconds,
                    WatchedAtUtc = e.WatchedAtUtc
                });
            }
            return copy;
        }

        private void PersistSnapshot(List<WatchHistoryEntry> snapshot)
        {
            lock (_fileIoLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string json = JsonSerializer.Serialize(snapshot, JsonOptions);
                    string tmpPath = _filePath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _filePath, true);
                }
                catch (Exception ex)
                {
                    Logger.Warn("HISTORY", "Falha ao salvar watch_history.json: " + ex.Message);
                }
            }
        }

        private void ScheduleSaveAsync(List<WatchHistoryEntry> snapshot)
        {
            _ = Task.Run(() => PersistSnapshot(snapshot));
        }

        public List<WatchHistoryEntry> GetAll()
        {
            lock (_lock)
            {
                return CloneSnapshotUnsafe();
            }
        }

        public WatchHistoryEntry GetEntry(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return null;
            lock (_lock)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    {
                        var e = _entries[i];
                        return new WatchHistoryEntry
                        {
                            VideoId = e.VideoId,
                            Title = e.Title,
                            Author = e.Author,
                            ThumbnailUrl = e.ThumbnailUrl,
                            DurationSeconds = e.DurationSeconds,
                            LastPositionSeconds = e.LastPositionSeconds,
                            WatchedAtUtc = e.WatchedAtUtc
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Verifica se o vídeo é elegível para Auto-Resume:
        /// DurationSeconds >= 300, LastPositionSeconds > 15 e LastPositionSeconds < (DurationSeconds - 30).
        /// </summary>
        public long GetResumePositionSeconds(string videoId, long knownDurationSeconds = 0)
        {
            var entry = GetEntry(videoId);
            if (entry == null) return 0;

            long effectiveDuration = Math.Max(entry.DurationSeconds, knownDurationSeconds);
            if (effectiveDuration < MinDurationForResumeSeconds) return 0;
            if (entry.LastPositionSeconds <= 15) return 0;
            if (entry.LastPositionSeconds >= (effectiveDuration - 30)) return 0;

            return entry.LastPositionSeconds;
        }

        public double GetProgressRatio(string videoId)
        {
            var entry = GetEntry(videoId);
            if (entry == null || entry.DurationSeconds <= 0 || entry.LastPositionSeconds <= 0)
                return 0.0;

            double ratio = (double)entry.LastPositionSeconds / entry.DurationSeconds;
            return Math.Max(0.0, Math.Min(1.0, ratio));
        }

        public void RecordVideoStarted(TrackItem video)
        {
            if (video == null || string.IsNullOrWhiteSpace(video.Id) || video.IsChannel || video.IsPlaylistCard)
                return;

            List<WatchHistoryEntry> snapshot;
            lock (_lock)
            {
                WatchHistoryEntry existing = null;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].VideoId, video.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = _entries[i];
                        _entries.RemoveAt(i);
                        break;
                    }
                }

                var entry = existing ?? new WatchHistoryEntry { VideoId = video.Id };
                if (!string.IsNullOrWhiteSpace(video.Title)) entry.Title = video.Title;
                if (!string.IsNullOrWhiteSpace(video.Artist)) entry.Author = video.Artist;
                if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl)) entry.ThumbnailUrl = video.ThumbnailUrl;
                if (video.DurationSeconds > 0) entry.DurationSeconds = video.DurationSeconds;
                entry.WatchedAtUtc = DateTime.UtcNow;

                _entries.Insert(0, entry);
                if (_entries.Count > MaxHistoryEntries)
                {
                    _entries.RemoveRange(MaxHistoryEntries, _entries.Count - MaxHistoryEntries);
                }

                snapshot = CloneSnapshotUnsafe();
            }

            ScheduleSaveAsync(snapshot);
        }

        public void UpdateVideoMetadata(string videoId, string title, string author, long durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;

            List<WatchHistoryEntry> snapshot = null;
            lock (_lock)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    {
                        var e = _entries[i];
                        bool changed = false;
                        if (!string.IsNullOrWhiteSpace(title) && e.Title != title) { e.Title = title; changed = true; }
                        if (!string.IsNullOrWhiteSpace(author) && e.Author != author) { e.Author = author; changed = true; }
                        if (durationSeconds > 0 && e.DurationSeconds != durationSeconds) { e.DurationSeconds = durationSeconds; changed = true; }
                        if (changed) snapshot = CloneSnapshotUnsafe();
                        break;
                    }
                }
            }

            if (snapshot != null) ScheduleSaveAsync(snapshot);
        }

        public void UpdatePlaybackProgress(string videoId, long positionSeconds, long durationSeconds, bool saveImmediatelySync = false)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;

            List<WatchHistoryEntry> snapshot = null;
            lock (_lock)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    {
                        var e = _entries[i];
                        if (durationSeconds > 0 && e.DurationSeconds <= 0)
                        {
                            e.DurationSeconds = durationSeconds;
                        }
                        long effectiveDuration = Math.Max(e.DurationSeconds, durationSeconds);
                        if (effectiveDuration >= MinDurationForResumeSeconds)
                        {
                            e.LastPositionSeconds = Math.Max(0, Math.Min(effectiveDuration, positionSeconds));
                            e.WatchedAtUtc = DateTime.UtcNow;
                            snapshot = CloneSnapshotUnsafe();
                        }
                        break;
                    }
                }
            }

            if (snapshot != null)
            {
                if (saveImmediatelySync)
                {
                    PersistSnapshot(snapshot);
                }
                else
                {
                    ScheduleSaveAsync(snapshot);
                }
            }
        }

        public void Flush()
        {
            List<WatchHistoryEntry> snapshot;
            lock (_lock)
            {
                snapshot = CloneSnapshotUnsafe();
            }
            PersistSnapshot(snapshot);
        }

        public List<TrackItem> GetHistoryAsTrackItems()
        {
            var list = new List<TrackItem>();
            var entries = GetAll();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                list.Add(new TrackItem
                {
                    Id = e.VideoId ?? string.Empty,
                    Title = !string.IsNullOrWhiteSpace(e.Title) ? e.Title : e.VideoId,
                    Artist = !string.IsNullOrWhiteSpace(e.Author) ? e.Author : "YouTube",
                    CustomThumbUrl = e.ThumbnailUrl ?? string.Empty,
                    DurationSeconds = (int)Math.Max(0, e.DurationSeconds),
                    Duration = e.DurationSeconds > 0 ? YouTubeService.FormatSeconds(e.DurationSeconds) : "--:--",
                    LastPositionSeconds = e.LastPositionSeconds,
                    ItemType = "video"
                });
            }
            return list;
        }
    }
}

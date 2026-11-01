using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace LiteTube
{
    public enum RepeatMode
    {
        None = 0,
        RepeatAll = 1,
        RepeatOne = 2
    }

    public class TrackItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string Duration { get; set; }
        public int DurationSeconds { get; set; }
        public bool IsAlbum { get; set; }
        public string PlaylistId { get; set; }

        // Channel & Rich Item Support ("video", "channel", "playlist")
        public string ItemType { get; set; }
        public string ChannelId { get; set; }
        public string CustomThumbUrl { get; set; }
        public string SubscriberCountText { get; set; }
        public string VideoCountText { get; set; }

        public TrackItem()
        {
            Id = "";
            Title = "";
            Artist = "YouTube";
            Album = "";
            Duration = "--:--";
            DurationSeconds = 0;
            IsAlbum = false;
            PlaylistId = "";
            ItemType = "video";
            ChannelId = "";
            CustomThumbUrl = "";
            SubscriberCountText = "";
            VideoCountText = "";
        }

        public bool IsChannel
        {
            get { return string.Equals(ItemType, "channel", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsPlaylistCard
        {
            get { return string.Equals(ItemType, "playlist", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsShort
        {
            get
            {
                if (IsChannel || IsPlaylistCard || IsAlbum) return false;
                if (string.Equals(ItemType, "short", StringComparison.OrdinalIgnoreCase)) return true;
                if (DurationSeconds > 0 && DurationSeconds <= 60) return true;
                if (!string.IsNullOrEmpty(Title))
                {
                    string t = Title.ToLowerInvariant();
                    if (t.Contains("#shorts") || t.Contains("#short") || t.Contains(" shorts") ||
                        t.StartsWith("shorts ") || t.Contains("#reels") || t.Contains("#tiktok"))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public string DisplayTitle
        {
            get
            {
                if (IsChannel) return "📺 CANAL: " + Title;
                if (IsPlaylistCard) return "📂 PLAYLIST: " + Title;
                if (IsAlbum) return "[ÁLBUM] " + Title;
                return Title;
            }
        }
    }

    public class SearchResultPage
    {
        public List<TrackItem> Items { get; set; }
        public string ContinuationToken { get; set; }
        public string Query { get; set; }

        public SearchResultPage()
        {
            Items = new List<TrackItem>();
            ContinuationToken = "";
            Query = "";
        }
    }

    public class ChannelPageData
    {
        public string ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string SubscriberText { get; set; }
        public string AvatarUrl { get; set; }
        public List<TrackItem> Items { get; set; }
        public string ContinuationToken { get; set; }
        public string ActiveTab { get; set; } // "videos" or "playlists"

        public ChannelPageData()
        {
            ChannelId = "";
            ChannelName = "Canal do YouTube";
            SubscriberText = "";
            AvatarUrl = "";
            Items = new List<TrackItem>();
            ContinuationToken = "";
            ActiveTab = "videos";
        }
    }

    public class UserPlaylist
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string RemoteUrl { get; set; }
        public List<TrackItem> Tracks { get; set; }

        public UserPlaylist()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "Nova Playlist";
            RemoteUrl = "";
            Tracks = new List<TrackItem>();
        }
    }

    public class AppConfig
    {
        public string BrowserForCookies { get; set; }
        public string CookiesFilePath { get; set; }

        // Independent Video & Music Volumes (1:1 Direct Linear Mapping)
        public double VideoVolume { get; set; }
        public bool IsVideoMuted { get; set; }
        public double MusicVolume { get; set; }
        public bool IsMusicMuted { get; set; }

        // Legacy fields kept only for automatic migration from older config.json
        public double? MasterVolume { get; set; }
        public bool? IsMasterMuted { get; set; }
        public double? Volume { get; set; }
        public bool? IsMuted { get; set; }
        public double? MaxVolumeCeiling { get; set; }
        public double? Balance { get; set; }
        public double? PlaybackSpeed { get; set; }

        public int VideoQualityHeight { get; set; }
        public bool AutoPauseMusicWhenVideoPlays { get; set; }
        public int MaxCacheSizeMB { get; set; }
        public int MaxVideoCacheSizeMB { get; set; }
        public int MaxThumbCacheSizeMB { get; set; }
        public int Repeat { get; set; }
        public bool Shuffle { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool AlwaysOnTopMiniPlayer { get; set; }
        public bool EnableGlobalHotkeys { get; set; }

        // ReVanced Features
        public bool HideShorts { get; set; }
        public bool EnableSponsorBlock { get; set; }
        public bool AutoSkipSponsors { get; set; }

        public List<TrackItem> Favorites { get; set; }
        public List<TrackItem> SavedVideos { get; set; }
        public List<UserPlaylist> Playlists { get; set; }

        public AppConfig()
        {
            BrowserForCookies = "firefox";
            CookiesFilePath = "";

            VideoVolume = 0.85; // Padrão: 85% para Vídeos
            IsVideoMuted = false;
            MusicVolume = 0.50; // Padrão: 50% para Música
            IsMusicMuted = false;

            VideoQualityHeight = 720;
            AutoPauseMusicWhenVideoPlays = true;
            MaxCacheSizeMB = 100;
            MaxVideoCacheSizeMB = 200;
            MaxThumbCacheSizeMB = 15;
            Repeat = (int)RepeatMode.None;
            Shuffle = false;
            MinimizeToTrayOnClose = true;
            AlwaysOnTopMiniPlayer = true;
            EnableGlobalHotkeys = true;

            HideShorts = true;
            EnableSponsorBlock = true;
            AutoSkipSponsors = true;

            Favorites = new List<TrackItem>();
            SavedVideos = new List<TrackItem>();
            Playlists = new List<UserPlaylist>();
        }

        private static string GetConfigPath()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        private static readonly object _saveLock = new object();

        public static AppConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                string tempPath = path + ".tmp";
                if (!File.Exists(path) && File.Exists(tempPath))
                {
                    try { File.Move(tempPath, path); } catch { }
                }

                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json) && File.Exists(tempPath))
                    {
                        json = File.ReadAllText(tempPath);
                    }

                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var cfg = serializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        if (cfg.Favorites == null) cfg.Favorites = new List<TrackItem>();
                        if (cfg.SavedVideos == null) cfg.SavedVideos = new List<TrackItem>();
                        if (cfg.Playlists == null) cfg.Playlists = new List<UserPlaylist>();
                        if (string.IsNullOrEmpty(cfg.BrowserForCookies)) cfg.BrowserForCookies = "firefox";

                        // Validate and migrate independent VideoVolume and MusicVolume
                        if (cfg.VideoVolume <= 0.001 || cfg.VideoVolume > 1.0)
                        {
                            if (cfg.MasterVolume.HasValue && cfg.MasterVolume.Value > 0.01 && cfg.MasterVolume.Value <= 1.0)
                                cfg.VideoVolume = cfg.MasterVolume.Value;
                            else
                                cfg.VideoVolume = 0.85;
                        }
                        if (cfg.MusicVolume <= 0.001 || cfg.MusicVolume > 1.0)
                        {
                            if (cfg.Volume.HasValue && cfg.Volume.Value > 0.01 && cfg.Volume.Value <= 1.0)
                                cfg.MusicVolume = cfg.Volume.Value;
                            else
                                cfg.MusicVolume = 0.50;
                        }

                        // Clear legacy fields after migration
                        cfg.MasterVolume = null;
                        cfg.IsMasterMuted = null;
                        cfg.Volume = null;
                        cfg.IsMuted = null;
                        cfg.MaxVolumeCeiling = null;
                        cfg.Balance = null;
                        cfg.PlaybackSpeed = null;

                        if (cfg.MaxCacheSizeMB < 15 || cfg.MaxCacheSizeMB > 2000) cfg.MaxCacheSizeMB = 100;
                        if (cfg.MaxVideoCacheSizeMB < 50 || cfg.MaxVideoCacheSizeMB > 2000) cfg.MaxVideoCacheSizeMB = 200;
                        if (cfg.MaxThumbCacheSizeMB < 5 || cfg.MaxThumbCacheSizeMB > 100) cfg.MaxThumbCacheSizeMB = 15;
                        if (cfg.VideoQualityHeight != 360 && cfg.VideoQualityHeight != 480 &&
                            cfg.VideoQualityHeight != 720 && cfg.VideoQualityHeight != 1080)
                        {
                            cfg.VideoQualityHeight = 720;
                        }

                        return cfg;
                    }
                }
            }
            catch
            {
                // Return default on error
            }
            return new AppConfig();
        }

        public void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    string path = GetConfigPath();
                    string tempPath = path + ".tmp";
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    string json = serializer.Serialize(this);
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(tempPath, path);
                }
                catch { }
            }
        }

        public bool IsFavorite(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return false;
            for (int i = 0; i < Favorites.Count; i++)
            {
                if (string.Equals(Favorites[i].Id, videoId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public bool ToggleFavorite(TrackItem track)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return false;
            for (int i = 0; i < Favorites.Count; i++)
            {
                if (string.Equals(Favorites[i].Id, track.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Favorites.RemoveAt(i);
                    Save();
                    return false;
                }
            }
            Favorites.Insert(0, track);
            Save();
            return true;
        }

        public bool IsSavedVideo(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return false;
            for (int i = 0; i < SavedVideos.Count; i++)
            {
                if (string.Equals(SavedVideos[i].Id, videoId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public bool ToggleSavedVideo(TrackItem video)
        {
            if (video == null || string.IsNullOrEmpty(video.Id)) return false;
            for (int i = 0; i < SavedVideos.Count; i++)
            {
                if (string.Equals(SavedVideos[i].Id, video.Id, StringComparison.OrdinalIgnoreCase))
                {
                    SavedVideos.RemoveAt(i);
                    Save();
                    return false;
                }
            }
            SavedVideos.Insert(0, video);
            Save();
            return true;
        }
    }
}

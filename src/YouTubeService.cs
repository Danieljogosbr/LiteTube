using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    public class YouTubeService
    {
        private readonly string _ytDlpPath;
        private readonly string _ffmpegPath;
        private readonly string _binDir;
        private readonly string _cacheDir;
        private readonly object _cacheLock = new object();

        // Cache em RAM limitado para URLs de avatares de canais (por ChannelId UC... e por Nome do Canal)
        private const int MaxAvatarCacheEntries = 500;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _channelAvatarMemoryCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _channelAvatarCacheOrder
            = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightAvatarResolves
            = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _avatarResolveSemaphore = new SemaphoreSlim(4, 4);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, DateTime ExpiresAtUtc)> _audioUrlMemoryCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, DateTime ExpiresAtUtc)>(StringComparer.OrdinalIgnoreCase);

        public static string GetCachedChannelAvatarUrl(string channelId, string channelName)
        {
            string url;
            if (!string.IsNullOrWhiteSpace(channelId) && _channelAvatarMemoryCache.TryGetValue(channelId.Trim(), out url) && !string.IsNullOrEmpty(url))
                return url;
            if (!string.IsNullOrWhiteSpace(channelName) && _channelAvatarMemoryCache.TryGetValue("name:" + channelName.Trim(), out url) && !string.IsNullOrEmpty(url))
                return url;
            return "";
        }

        public static void CacheChannelAvatarUrl(string channelId, string channelName, string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl)) return;
            avatarUrl = NormalizeAvatarUrl(avatarUrl);
            if (string.IsNullOrEmpty(avatarUrl)) return;

            if (!string.IsNullOrWhiteSpace(channelId))
            {
                StoreAvatarCacheEntry(channelId.Trim(), avatarUrl);
            }
            if (!string.IsNullOrWhiteSpace(channelName) && !string.Equals(channelName.Trim(), "YouTube", StringComparison.OrdinalIgnoreCase))
            {
                StoreAvatarCacheEntry("name:" + channelName.Trim(), avatarUrl);
            }
        }

        private static void StoreAvatarCacheEntry(string key, string avatarUrl)
        {
            bool isNew = !_channelAvatarMemoryCache.ContainsKey(key);
            _channelAvatarMemoryCache[key] = avatarUrl;
            if (isNew)
            {
                _channelAvatarCacheOrder.Enqueue(key);
                while (_channelAvatarMemoryCache.Count > MaxAvatarCacheEntries && _channelAvatarCacheOrder.TryDequeue(out string oldKey))
                {
                    _channelAvatarMemoryCache.TryRemove(oldKey, out _);
                }
            }
        }

        public static string NormalizeAvatarUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return "";
            string url = UnescapeJsonString(rawUrl).Replace("\\/", "/").Replace("\\u0026", "&").Trim();
            if (url.StartsWith("//")) url = "https:" + url;
            // Descarta banners horizontais de canal (fcrop64 / =w1060 / =w2120) que compartilham o domínio yt3.googleusercontent.com
            if (url.IndexOf("fcrop64", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("=w1060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("=w1707", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("=w2120", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("=w2560", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "";
            }
            // Melhora nitidez de miniaturas minúsculas (=s48- ou =s68- -> =s176-)
            url = Regex.Replace(url, @"=s(?:32|48|68|88)-c", "=s176-c");
            return url;
        }

        public static string ExtractChannelAvatarUrl(string block)
        {
            if (string.IsNullOrEmpty(block)) return "";

            // 1. Prioriza blocos explícitos de avatar de canal
            string[] priorityMarkers = new string[]
            {
                "\"channelMetadataRenderer\"",
                "\"c4TabbedHeaderRenderer\"",
                "\"decoratedAvatarViewModel\"",
                "\"channelThumbnailSupportedRenderers\"",
                "\"channelThumbnailWithLinkRenderer\"",
                "\"avatar\""
            };

            for (int m = 0; m < priorityMarkers.Length; m++)
            {
                int idx = block.IndexOf(priorityMarkers[m], StringComparison.Ordinal);
                if (idx >= 0)
                {
                    string sub = block.Substring(idx, Math.Min(4500, block.Length - idx));
                    string found = MatchFirstValidAvatarUrl(sub);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }

            // 2. Fallback para qualquer URL de avatar válida no bloco
            return MatchFirstValidAvatarUrl(block);
        }

        private static string MatchFirstValidAvatarUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var matches = Regex.Matches(
                text,
                "\"url\"\\s*:\\s*\"((?:https?:)?(?:\\\\?/){2}[a-zA-Z0-9_.-]+?\\.(?:ggpht|googleusercontent)\\.com(?:\\\\?/[^\"\\\\]|[^\"])+)\""
            );

            for (int i = 0; i < matches.Count; i++)
            {
                string candidate = NormalizeAvatarUrl(matches[i].Groups[1].Value);
                if (!string.IsNullOrEmpty(candidate))
                {
                    return candidate;
                }
            }
            return "";
        }

        public static async Task<string> ResolveChannelAvatarUrlStaticAsync(string channelId, string channelName)
        {
            string cached = GetCachedChannelAvatarUrl(channelId, channelName);
            if (!string.IsNullOrEmpty(cached)) return cached;

            string cleanId = (channelId ?? "").Trim();
            string cleanName = (channelName ?? "").Trim();
            if (cleanId.StartsWith("CH_", StringComparison.OrdinalIgnoreCase))
                cleanId = cleanId.Substring(3);

            string flightKey = !string.IsNullOrEmpty(cleanId) && cleanId.StartsWith("UC")
                ? cleanId
                : (!string.IsNullOrEmpty(cleanName) && !string.Equals(cleanName, "YouTube", StringComparison.OrdinalIgnoreCase) ? ("name:" + cleanName) : "");

            if (string.IsNullOrEmpty(flightKey)) return "";

            return await _inFlightAvatarResolves.GetOrAdd(flightKey, _ => Task.Run(async () =>
            {
                try
                {
                    await _avatarResolveSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        string recheck = GetCachedChannelAvatarUrl(cleanId, cleanName);
                        if (!string.IsNullOrEmpty(recheck)) return recheck;

                        var headers = new Dictionary<string, string>
                        {
                            { "X-YouTube-Client-Name", "1" },
                            { "X-YouTube-Client-Version", "2.20240530.01.00" },
                            { "Origin", "https://www.youtube.com" }
                        };

                        // Tentativa 1: Se tivermos o browseId oficial do canal (UC...), consulta o endpoint browse
                        if (!string.IsNullOrEmpty(cleanId) && cleanId.StartsWith("UC"))
                        {
                            string browsePayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                                   EscapeJson(cleanId) + "\"}";
                            string browseJson = await HttpPipeline.PostJsonAsync(
                                "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false",
                                browsePayload,
                                "https://www.youtube.com/channel/" + cleanId,
                                headers).ConfigureAwait(false);

                            string av = ExtractChannelAvatarUrl(browseJson);
                            if (!string.IsNullOrEmpty(av))
                            {
                                CacheChannelAvatarUrl(cleanId, cleanName, av);
                                return av;
                            }
                        }

                        // Tentativa 2: Busca rápida pelo nome do canal ou ID no InnerTube Search
                        string query = !string.IsNullOrEmpty(cleanName) && !string.Equals(cleanName, "YouTube", StringComparison.OrdinalIgnoreCase)
                            ? cleanName
                            : cleanId;

                        if (!string.IsNullOrEmpty(query))
                        {
                            string searchPayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"" +
                                                   EscapeJson(query) + "\",\"params\":\"EgIQAg==\"}";
                            string searchJson = await HttpPipeline.PostJsonAsync(
                                "https://www.youtube.com/youtubei/v1/search?prettyPrint=false",
                                searchPayload,
                                "https://www.youtube.com/",
                                headers).ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(searchJson))
                            {
                                string[] chBlocks = searchJson.Split(new string[] { "\"channelRenderer\":" }, StringSplitOptions.None);
                                if (chBlocks.Length > 1)
                                {
                                    string b = chBlocks[1];
                                    if (b.Length > 5000) b = b.Substring(0, 5000);
                                    string foundCid = ExtractRegex(b, "\"channelId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                                    string av = ExtractChannelAvatarUrl(b);
                                    if (!string.IsNullOrEmpty(av))
                                    {
                                        CacheChannelAvatarUrl(!string.IsNullOrEmpty(foundCid) ? foundCid : cleanId, cleanName, av);
                                        return av;
                                    }
                                }

                                string fallbackAv = ExtractChannelAvatarUrl(searchJson);
                                if (!string.IsNullOrEmpty(fallbackAv))
                                {
                                    CacheChannelAvatarUrl(cleanId, cleanName, fallbackAv);
                                    return fallbackAv;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _avatarResolveSemaphore.Release();
                    }
                }
                catch { }
                finally
                {
                    Task<string> removed;
                    _inFlightAvatarResolves.TryRemove(flightKey, out removed);
                }
                return "";
            })).ConfigureAwait(false);
        }

        static YouTubeService()
        {
            try
            {
                // Delega para o sistema operacional (.NET 4.8 / 4.8.1) e habilita TLS 1.2 e TLS 1.3 nativos
                ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault | SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
            }
            catch
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                }
                catch { }
            }
        }

        public YouTubeService(string customBaseDir = null)
        {
            string baseDir = !string.IsNullOrEmpty(customBaseDir) ? customBaseDir : AppDomain.CurrentDomain.BaseDirectory;
            _ytDlpPath = YtDlpResolver.GetYtDlpBinaryPath();
            _binDir = YtDlpResolver.GetYtDlpDirectory();
            _ffmpegPath = FFmpegResolver.ResolveFFmpegExecutablePath();
            _cacheDir = Path.Combine(baseDir, "data", "cache");

            if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir);

            EnforceCacheSizeLimit(100);
        }

        /// <summary>
        /// Ultra-fast search using YouTube's internal InnerTube API (~300ms).
        /// Returns video items + channel cards + continuation token for infinite scrolling.
        /// </summary>
        public async Task<SearchResultPage> SearchPageAsync(string query, string filterType, AppConfig config)
        {
            var page = new SearchResultPage { Query = query ?? "" };
            if (string.IsNullOrWhiteSpace(query))
                return page;

            query = query.Trim();

            // Check if the user pasted a direct YouTube / YouTube Music URL
            if (query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith("www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith("music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith("youtu.be/", StringComparison.OrdinalIgnoreCase))
            {
                var res = await FetchUrlOrPlaylistAsync(query, config, false);
                page.Items = res.Item1;
                return page;
            }

            return await Task.Run(() =>
            {
                bool isVideoMode = (filterType == "videos");
                try
                {
                    string effectiveQuery = query;
                    if (filterType == "albums")
                    {
                        effectiveQuery = query + " full album";
                    }
                    else if (filterType == "songs")
                    {
                        effectiveQuery = query + " topic official audio";
                    }

                    string escapedQuery = EscapeJson(effectiveQuery);
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"" + escapedQuery + "\"}";

                    string jsonResponse = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", payload);
                    page = ParseInnerTubeSearchPage(jsonResponse, isVideoMode, isVideoMode);
                    page.Query = query;
                }
                catch { }

                // Count how many actual playable videos we got (excluding channel cards)
                int videoCount = 0;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    if (!page.Items[i].IsChannel && !page.Items[i].IsPlaylistCard)
                        videoCount++;
                }

                // If InnerTube returned 0 videos, append fallback from yt-dlp so videos ALWAYS appear
                if (videoCount == 0 && File.Exists(_ytDlpPath))
                {
                    var fallbackVideos = FallbackYtDlpSearch(query, 24);
                    page.Items.AddRange(fallbackVideos);
                }

                return page;
            });
        }

        /// <summary>
        /// Infinite Scroll Continuation: Fetches the next batch of search results using YouTube's continuationToken.
        /// </summary>
        public async Task<SearchResultPage> FetchSearchContinuationAsync(string continuationToken, bool isVideoMode)
        {
            var page = new SearchResultPage();
            if (string.IsNullOrWhiteSpace(continuationToken))
                return page;

            return await Task.Run(() =>
            {
                try
                {
                    string escapedToken = EscapeJson(continuationToken);
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"continuation\":\"" + escapedToken + "\"}";
                    string jsonResponse = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", payload);
                    page = ParseInnerTubeSearchPage(jsonResponse, isVideoMode, false);
                }
                catch { }
                return page;
            });
        }

        /// <summary>
        /// Fetches a Channel's "home" (Início), "videos" (Vídeos), or "playlists" (Playlists) tab.
        /// Strictly isolates content belonging ONLY to the target channel (UC... / UU...).
        /// </summary>
        public async Task<ChannelPageData> FetchChannelPageAsync(string channelId, string channelName, string tab)
        {
            if (string.IsNullOrWhiteSpace(tab)) tab = "home";

            return await Task.Run(() =>
            {
                var data = new ChannelPageData
                {
                    ChannelId = channelId ?? "",
                    ChannelName = !string.IsNullOrWhiteSpace(channelName) ? channelName : "Canal do YouTube",
                    ActiveTab = tab
                };

                try
                {
                    // 1. Always ensure we have the exact UC... Channel ID
                    if (string.IsNullOrWhiteSpace(data.ChannelId) || !data.ChannelId.StartsWith("UC"))
                    {
                        ResolveChannelIdFromName(data);
                    }

                    string uploadsBrowseId = (!string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC") && data.ChannelId.Length > 4)
                        ? ("VLUU" + data.ChannelId.Substring(2))
                        : "";

                    if (tab == "home")
                    {
                        var homeItems = new List<TrackItem>();
                        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Step A: Query Channel Home browseId (extract avatar, subscribers, header, and home shelves)
                        if (!string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string homePayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                                 EscapeJson(data.ChannelId) + "\"}";
                            string homeJson = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", homePayload, null, "https://www.youtube.com/channel/" + data.ChannelId);
                            ExtractChannelHeaderInfo(homeJson, data);

                            var homeVideos = ParseInnerTubeSearchPage(homeJson, true, false);
                            foreach (var v in homeVideos.Items)
                            {
                                if (IsShortsVideo(v.Title, v.DurationSeconds, "")) continue;
                                if (string.IsNullOrEmpty(v.Artist) || v.Artist == "YouTube")
                                {
                                    v.Artist = data.ChannelName;
                                }
                                if (string.IsNullOrEmpty(v.ChannelId))
                                {
                                    v.ChannelId = data.ChannelId;
                                }
                                if (seenIds.Add(v.Id)) homeItems.Add(v);
                            }

                            if (string.IsNullOrEmpty(data.ContinuationToken) && !string.IsNullOrEmpty(homeVideos.ContinuationToken))
                            {
                                data.ContinuationToken = "BROWSE:" + homeVideos.ContinuationToken;
                            }
                        }

                        // Step B: Fallback if channel home returned fewer than 5 items - query official videos tab ("EgZ2aWRlb3PyBgQKAjoA")
                        if (homeItems.Count < 5 && !string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string vPayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                              EscapeJson(data.ChannelId) + "\",\"params\":\"EgZ2aWRlb3PyBgQKAjoA\"}";
                            string vJson = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", vPayload, null, "https://www.youtube.com/channel/" + data.ChannelId);
                            var vPage = ParseInnerTubeSearchPage(vJson, true, false);
                            foreach (var v in vPage.Items)
                            {
                                if (IsShortsVideo(v.Title, v.DurationSeconds, "")) continue;
                                v.Artist = data.ChannelName;
                                v.ChannelId = data.ChannelId;
                                if (seenIds.Add(v.Id)) homeItems.Add(v);
                            }
                            if (string.IsNullOrEmpty(data.ContinuationToken) && !string.IsNullOrEmpty(vPage.ContinuationToken))
                            {
                                data.ContinuationToken = "BROWSE:" + vPage.ContinuationToken;
                            }
                        }

                        // Step C: Fallback to official uploads playlist ("VLUU...") if still few items
                        if (homeItems.Count < 5 && !string.IsNullOrEmpty(uploadsBrowseId))
                        {
                            string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                             EscapeJson(uploadsBrowseId) + "\"}";
                            string json = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", payload);
                            var uploadsPage = ParseInnerTubeSearchPage(json, true, false);
                            foreach (var v in uploadsPage.Items)
                            {
                                if (IsShortsVideo(v.Title, v.DurationSeconds, "")) continue;
                                v.Artist = data.ChannelName;
                                v.ChannelId = data.ChannelId;
                                if (seenIds.Add(v.Id)) homeItems.Add(v);
                            }
                        }

                        // Step D: Ultimate fallback to yt-dlp on UU... uploads playlist if 0 items
                        if (homeItems.Count == 0 && File.Exists(_ytDlpPath) && !string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string uuListId = "UU" + data.ChannelId.Substring(2);
                            string uuUrl = "https://www.youtube.com/playlist?list=" + uuListId;
                            string args = string.Format("--yes-playlist --flat-playlist --playlist-end 40 --print \"%(id)s\t%(title)s\t%(uploader)s\t%(duration_string)s\" --no-warnings \"{0}\"", uuUrl);
                            string output = RunYtDlpProcess(args, 18000);
                            var flat = ParseFlatPlaylistOutput(output);
                            foreach (var v in flat)
                            {
                                v.Artist = data.ChannelName;
                                v.ChannelId = data.ChannelId;
                                if (seenIds.Add(v.Id)) homeItems.Add(v);
                            }
                        }

                        data.Items = homeItems;
                    }
                    else if (tab == "videos")
                    {
                        // Layer 1: Official Channel Uploads Playlist ("VLUU...") — 100% exclusively videos uploaded by this channel!
                        if (!string.IsNullOrEmpty(uploadsBrowseId))
                        {
                            string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                             EscapeJson(uploadsBrowseId) + "\"}";
                            string json = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", payload);
                            var parsed = ParseInnerTubeSearchPage(json, true, false);
                            for (int i = 0; i < parsed.Items.Count; i++)
                            {
                                parsed.Items[i].Artist = data.ChannelName;
                                parsed.Items[i].ChannelId = data.ChannelId;
                            }
                            data.Items = parsed.Items;
                            data.ContinuationToken = !string.IsNullOrEmpty(parsed.ContinuationToken)
                                ? "BROWSE:" + parsed.ContinuationToken
                                : "";
                        }

                        // Layer 2: Official InnerTube Channel Videos Tab ("EgZ2aWRlb3PyBgQKAjoA")
                        if (data.Items.Count == 0 && !string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                             EscapeJson(data.ChannelId) + "\",\"params\":\"EgZ2aWRlb3PyBgQKAjoA\"}";
                            string json = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", payload);
                            ExtractChannelHeaderInfo(json, data);
                            var parsed = ParseInnerTubeSearchPage(json, true, false);
                            for (int i = 0; i < parsed.Items.Count; i++)
                            {
                                parsed.Items[i].Artist = data.ChannelName;
                                parsed.Items[i].ChannelId = data.ChannelId;
                            }
                            data.Items = parsed.Items;
                            data.ContinuationToken = !string.IsNullOrEmpty(parsed.ContinuationToken)
                                ? "BROWSE:" + parsed.ContinuationToken
                                : "";
                        }

                        // Layer 3: yt-dlp on the channel's official UU... uploads playlist (100% pure channel videos)
                        if (data.Items.Count == 0 && File.Exists(_ytDlpPath) && !string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string uuListId = "UU" + data.ChannelId.Substring(2);
                            string uuUrl = "https://www.youtube.com/playlist?list=" + uuListId;
                            string args = string.Format("--yes-playlist --flat-playlist --playlist-end 50 --print \"%(id)s\t%(title)s\t%(uploader)s\t%(duration_string)s\" --no-warnings \"{0}\"", uuUrl);
                            string output = RunYtDlpProcess(args, 18000);
                            var flat = ParseFlatPlaylistOutput(output);
                            for (int i = 0; i < flat.Count; i++)
                            {
                                flat[i].Artist = data.ChannelName;
                                flat[i].ChannelId = data.ChannelId;
                            }
                            data.Items = flat;
                        }
                    }
                    else if (tab == "playlists")
                    {
                        var playlists = new List<TrackItem>();
                        var seenPl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Step A: Official Channel Playlists Tab ("EglwbGF5bGlzdHPyBgQKAkIA")
                        if (!string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                        {
                            string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                             EscapeJson(data.ChannelId) + "\",\"params\":\"EglwbGF5bGlzdHPyBgQKAkIA\"}";
                            string json = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", payload);
                            ExtractChannelHeaderInfo(json, data);
                            var tabPl = ParsePlaylistsFromInnerTubeJson(json, data.ChannelName, data.ChannelId, false);
                            foreach (var pl in tabPl)
                            {
                                if (seenPl.Add(pl.PlaylistId))
                                    playlists.Add(pl);
                            }

                            // Also check Channel Home shelves for playlists created by this channel
                            if (playlists.Count == 0)
                            {
                                string homePayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" +
                                                     EscapeJson(data.ChannelId) + "\"}";
                                string homeJson = PostInnerTube("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", homePayload);
                                var homePl = ParsePlaylistsFromInnerTubeJson(homeJson, data.ChannelName, data.ChannelId, false);
                                foreach (var pl in homePl)
                                {
                                    if (seenPl.Add(pl.PlaylistId))
                                        playlists.Add(pl);
                                }
                            }
                        }

                        // Step B: Search YouTube with Playlist filter ("EgIQAw=="), strictly matching this channel!
                        if (playlists.Count < 3 && !string.IsNullOrWhiteSpace(data.ChannelName))
                        {
                            string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"" +
                                             EscapeJson(data.ChannelName) + "\",\"params\":\"EgIQAw==\"}";
                            string json = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", payload);
                            var searchPl = ParsePlaylistsFromInnerTubeJson(json, data.ChannelName, data.ChannelId, true);
                            foreach (var pl in searchPl)
                            {
                                if (seenPl.Add(pl.PlaylistId))
                                    playlists.Add(pl);
                            }
                        }

                        // Always include the channel's complete Uploads Playlist ("Todos os Vídeos do Canal") at top if available
                        if (!string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC") && data.ChannelId.Length > 4)
                        {
                            string uuId = "UU" + data.ChannelId.Substring(2);
                            if (seenPl.Add(uuId))
                            {
                                playlists.Insert(0, new TrackItem
                                {
                                    Id = "PL_" + uuId,
                                    PlaylistId = uuId,
                                    ChannelId = data.ChannelId,
                                    Title = "Todos os Envios de " + data.ChannelName,
                                    Artist = data.ChannelName + " • Playlist Oficial de Envios",
                                    Duration = "PLAYLIST",
                                    ItemType = "playlist"
                                });
                            }
                        }

                        data.Items = playlists;
                    }

                    // Garante que o AvatarUrl do canal esteja preenchido (via cache em RAM ou resolução dedicada)
                    if (string.IsNullOrEmpty(data.AvatarUrl))
                    {
                        data.AvatarUrl = GetCachedChannelAvatarUrl(data.ChannelId, data.ChannelName);
                    }
                    if (string.IsNullOrEmpty(data.AvatarUrl) && !string.IsNullOrEmpty(data.ChannelId) && data.ChannelId.StartsWith("UC"))
                    {
                        data.AvatarUrl = ResolveChannelAvatarUrlStaticAsync(data.ChannelId, data.ChannelName).GetAwaiter().GetResult();
                    }
                    if (!string.IsNullOrEmpty(data.AvatarUrl))
                    {
                        CacheChannelAvatarUrl(data.ChannelId, data.ChannelName, data.AvatarUrl);
                        if (data.Items != null)
                        {
                            for (int i = 0; i < data.Items.Count; i++)
                            {
                                if (data.Items[i] != null && string.IsNullOrEmpty(data.Items[i].ChannelAvatarUrl))
                                {
                                    data.Items[i].ChannelAvatarUrl = data.AvatarUrl;
                                }
                            }
                        }
                    }
                }
                catch { }

                return data;
            });
        }

        /// <summary>
        /// <summary>
        /// Fetches related/recommended videos + engagement details (subscribers, likes, comments count & continuation)
        /// from YouTube InnerTube Watch Next API in a single fast request (~150ms).
        /// </summary>
        public async Task<WatchNextDetails> FetchWatchNextDetailsAsync(string videoId, AppConfig config, int maxItems = 35)
        {
            var details = new WatchNextDetails();
            if (string.IsNullOrWhiteSpace(videoId)) return details;

            string safeId = Regex.Replace(videoId, "[^a-zA-Z0-9_-]", "");
            if (string.IsNullOrEmpty(safeId)) return details;

            return await Task.Run(() =>
            {
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenIds.Add(safeId);

                try
                {
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"videoId\":\"" +
                                     EscapeJson(safeId) + "\"}";
                    string json = PostInnerTube("https://www.youtube.com/youtubei/v1/next?prettyPrint=false", payload, config, "https://www.youtube.com/watch?v=" + safeId);

                    if (!string.IsNullOrEmpty(json))
                    {
                        string sub = ExtractFieldText(json, "\"subscriberCountText\"", 350);
                        if (!string.IsNullOrEmpty(sub)) details.SubscriberCountText = UnescapeJsonString(sub);

                        var parsed = ParseInnerTubeSearchPage(json, true, false);
                        foreach (var item in parsed.Items)
                        {
                            if (string.IsNullOrEmpty(item.Id) || item.Id == safeId) continue;
                            if (item.IsChannel || item.IsPlaylistCard) continue;
                            if (IsShortsVideo(item.Title, item.DurationSeconds, "")) continue;
                            if (seenIds.Add(item.Id))
                            {
                                details.RelatedVideos.Add(item);
                                if (details.RelatedVideos.Count >= maxItems) break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("YTSERVICE", "InnerTube next error para " + safeId + ": " + ex.Message);
                }

                if (details.RelatedVideos.Count < 5)
                {
                    try
                    {
                        var searchRes = SearchPageAsync(safeId, "videos", config).GetAwaiter().GetResult();
                        if (searchRes != null && searchRes.Items != null)
                        {
                            foreach (var item in searchRes.Items)
                            {
                                if (string.IsNullOrEmpty(item.Id) || item.Id == safeId) continue;
                                if (item.IsChannel || item.IsPlaylistCard) continue;
                                if (IsShortsVideo(item.Title, item.DurationSeconds, "")) continue;
                                if (seenIds.Add(item.Id))
                                {
                                    details.RelatedVideos.Add(item);
                                    if (details.RelatedVideos.Count >= maxItems) break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                return details;
            });
        }

        /// <summary>
        /// Infinite scroll inside a Channel's Videos tab.
        /// </summary>
        public async Task<SearchResultPage> FetchChannelContinuationAsync(string prefixedToken, string channelId, string channelName)
        {
            var page = new SearchResultPage();
            if (string.IsNullOrWhiteSpace(prefixedToken)) return page;

            return await Task.Run(() =>
            {
                try
                {
                    bool isSearchFallback = prefixedToken.StartsWith("SEARCH:");
                    string rawToken = prefixedToken;
                    if (rawToken.StartsWith("BROWSE:")) rawToken = rawToken.Substring("BROWSE:".Length);
                    else if (rawToken.StartsWith("SEARCH:")) rawToken = rawToken.Substring("SEARCH:".Length);

                    string endpoint = isSearchFallback
                        ? "https://www.youtube.com/youtubei/v1/search?prettyPrint=false"
                        : "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";

                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"continuation\":\"" +
                                     EscapeJson(rawToken) + "\"}";
                    string json = PostInnerTube(endpoint, payload);
                    var parsed = ParseInnerTubeSearchPage(json, true, false);
                    for (int i = 0; i < parsed.Items.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(channelName) &&
                            (string.IsNullOrEmpty(parsed.Items[i].Artist) || parsed.Items[i].Artist == "YouTube"))
                        {
                            parsed.Items[i].Artist = channelName;
                        }
                        if (!string.IsNullOrEmpty(channelId) && string.IsNullOrEmpty(parsed.Items[i].ChannelId))
                        {
                            parsed.Items[i].ChannelId = channelId;
                        }
                    }

                    page.Items = parsed.Items;
                    if (!string.IsNullOrEmpty(parsed.ContinuationToken))
                    {
                        page.ContinuationToken = (isSearchFallback ? "SEARCH:" : "BROWSE:") + parsed.ContinuationToken;
                    }
                }
                catch { }
                return page;
            });
        }

        private void ResolveChannelIdFromName(ChannelPageData data)
        {
            if (string.IsNullOrWhiteSpace(data.ChannelName)) return;
            try
            {
                string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"" +
                                 EscapeJson(data.ChannelName) + "\"}";
                string json = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", payload);
                if (string.IsNullOrEmpty(json)) return;

                string[] chBlocks = json.Split(new string[] { "\"channelRenderer\":" }, StringSplitOptions.None);
                if (chBlocks.Length > 1)
                {
                    string b = chBlocks[1];
                    if (b.Length > 5000) b = b.Substring(0, 5000);
                    string cid = ExtractRegex(b, "\"channelId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                    if (!string.IsNullOrEmpty(cid))
                    {
                        data.ChannelId = cid;
                        string sub = ExtractFieldText(b, "\"videoCountText\"", 350);
                        if (string.IsNullOrEmpty(sub)) sub = ExtractFieldText(b, "\"subscriberCountText\"", 350);
                        if (!string.IsNullOrEmpty(sub)) data.SubscriberText = UnescapeJsonString(sub);
                        string av = ExtractChannelAvatarUrl(b);
                        if (!string.IsNullOrEmpty(av))
                        {
                            data.AvatarUrl = av;
                            CacheChannelAvatarUrl(cid, data.ChannelName, av);
                        }
                        return;
                    }
                }

                string firstBrowseId = ExtractRegex(json, "\"browseId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                if (!string.IsNullOrEmpty(firstBrowseId))
                {
                    data.ChannelId = firstBrowseId;
                }
            }
            catch { }
        }

        private void ExtractChannelHeaderInfo(string json, ChannelPageData data)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                // Procura channelMetadataRenderer em todo o JSON (frequentemente posicionado após contents no browse do YouTube)
                int metaIdx = json.IndexOf("\"channelMetadataRenderer\"", StringComparison.Ordinal);
                if (metaIdx >= 0)
                {
                    string metaBlock = json.Substring(metaIdx, Math.Min(4000, json.Length - metaIdx));
                    string title = ExtractRegex(metaBlock, "\"title\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (!string.IsNullOrEmpty(title))
                    {
                        data.ChannelName = UnescapeJsonString(title);
                    }
                    string metaAv = ExtractChannelAvatarUrl(metaBlock);
                    if (!string.IsNullOrEmpty(metaAv))
                    {
                        data.AvatarUrl = metaAv;
                    }
                }

                string sub = ExtractFieldText(json, "\"subscriberCountText\"", 350);
                if (string.IsNullOrEmpty(sub))
                {
                    sub = ExtractRegex(json, "\"content\"\\s*:\\s*\"([0-9.,]+\\s*(?:mil|mi| inscritos|subscribers)[^\"]*)\"");
                }
                if (!string.IsNullOrEmpty(sub))
                {
                    data.SubscriberText = UnescapeJsonString(sub);
                }

                if (string.IsNullOrEmpty(data.AvatarUrl))
                {
                    string av = ExtractChannelAvatarUrl(json);
                    if (!string.IsNullOrEmpty(av))
                    {
                        data.AvatarUrl = av;
                    }
                }

                if (!string.IsNullOrEmpty(data.AvatarUrl))
                {
                    CacheChannelAvatarUrl(data.ChannelId, data.ChannelName, data.AvatarUrl);
                }
            }
            catch { }
        }

        private string PostInnerTube(string url, string jsonPayload, AppConfig config = null, string referer = null)
        {
            var headers = GoogleSessionService.Shared.BuildWebHeaders(config, out _);
            string refUrl = !string.IsNullOrEmpty(referer) ? referer : "https://www.youtube.com/";
            return HttpPipeline.PostJson(url, jsonPayload, refUrl, headers);
        }

        private string PostInnerTubeMusic(string jsonPayload, AppConfig config = null, string endpointUrl = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false")
        {
            var headers = GoogleSessionService.Shared.BuildMusicHeaders(config, out _);
            return HttpPipeline.PostJson(endpointUrl, jsonPayload, "https://music.youtube.com/", headers);
        }

        /// <summary>
        /// Fetches the user's personalized YouTube Music feed ("FEmusic_home" via WEB_REMIX client),
        /// parsing "musicCarouselShelfRenderer" shelves ("Ouvir novamente", "Seu Supermix", personalized carousels)
        /// into TrackItem instances, with automatic fallback to music trends if cookies are absent or expired.
        /// </summary>
        public async Task<Tuple<List<TrackItem>, string>> FetchPersonalizedMusicFeedAsync(AppConfig config)
        {
            return await Task.Run(() =>
            {
                var list = new List<TrackItem>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool usedFallback = false;

                try
                {
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"FEmusic_home\"}";
                    string json = PostInnerTubeMusic(payload, config);

                    if (!string.IsNullOrEmpty(json))
                    {
                        var parsed = ParseMusicCarouselShelves(json, seenIds);
                        if (parsed.Count > 0)
                        {
                            list.AddRange(parsed);
                        }
                    }

                    // Check if user had cookies or if response returned too few personalized tracks
                    string cookieCheck = GoogleSessionService.Shared.ResolveCookieHeader(config);
                    bool hasAuthSession = !string.IsNullOrEmpty(cookieCheck) &&
                                          (cookieCheck.IndexOf("SAPISID", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           cookieCheck.IndexOf("LOGIN_INFO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           cookieCheck.IndexOf("SSID", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!hasAuthSession && list.Count < 8)
                    {
                        usedFallback = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("YTMUSIC", "Erro ao buscar feed personalizado FEmusic_home: " + ex.Message);
                    usedFallback = true;
                }

                // Fallback: se os cookies estiverem ausentes, a sessão expirar ou FEmusic_home retornar vazio, busca tendências musicais
                if (list.Count == 0 || usedFallback)
                {
                    try
                    {
                        string explorePayload = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"FEmusic_explore\"}";
                        string exploreJson = PostInnerTubeMusic(explorePayload, config);
                        var exploreTracks = ParseMusicCarouselShelves(exploreJson, seenIds);
                        if (exploreTracks.Count > 0)
                        {
                            list.AddRange(exploreTracks);
                            usedFallback = true;
                        }
                    }
                    catch { }

                    if (list.Count < 10)
                    {
                        try
                        {
                            string trendPayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"top músicas mais tocadas brasil oficial audio\"}";
                            string trendJson = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", trendPayload, config);
                            var trendPage = ParseInnerTubeSearchPage(trendJson, false, false);
                            if (trendPage != null && trendPage.Items != null)
                            {
                                foreach (var t in trendPage.Items)
                                {
                                    if (t != null && !t.IsChannel && !t.IsPlaylistCard && seenIds.Add(t.Id))
                                    {
                                        list.Add(t);
                                        if (list.Count >= 45) break;
                                    }
                                }
                                usedFallback = true;
                            }
                        }
                        catch { }
                    }
                }

                string status = list.Count > 0
                    ? (usedFallback ? "Tendências musicais carregadas (conecte os cookies para personalizar o Supermix)." : "OK")
                    : "Não foi possível carregar o feed do YouTube Music.";

                return Tuple.Create(list, status);
            });
        }

        /// <summary>
        /// Fetches official YouTube Music charts, top songs, and new releases using browseId "FEmusic_explore" via WEB_REMIX.
        /// </summary>
        public async Task<Tuple<List<TrackItem>, string>> FetchMusicExploreFeedAsync(AppConfig config)
        {
            return await Task.Run(() =>
            {
                var list = new List<TrackItem>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"FEmusic_explore\"}";
                    string json = PostInnerTubeMusic(payload, config);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var exploreItems = ParseMusicCarouselShelves(json, seenIds);
                        if (exploreItems.Count > 0)
                        {
                            list.AddRange(exploreItems);
                        }
                    }

                    // Also query FEmusic_charts if we want more official chart tracks
                    if (list.Count < 15)
                    {
                        string chartsPayload = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"FEmusic_charts\"}";
                        string chartsJson = PostInnerTubeMusic(chartsPayload, config);
                        if (!string.IsNullOrEmpty(chartsJson))
                        {
                            var chartItems = ParseMusicCarouselShelves(chartsJson, seenIds);
                            if (chartItems.Count > 0)
                            {
                                list.AddRange(chartItems);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("YTMUSIC", "Erro ao buscar FEmusic_explore: " + ex.Message);
                }

                // Fallback to YouTube Music official releases / charts search if FEmusic_explore returned fewer than 8 playable tracks
                if (list.Count < 8)
                {
                    try
                    {
                        string searchPayload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"lançamentos musicais paradas youtube music oficial\"}";
                        string searchJson = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", searchPayload, config);
                        var page = ParseInnerTubeSearchPage(searchJson, false, false);
                        if (page != null && page.Items != null)
                        {
                            foreach (var t in page.Items)
                            {
                                if (t != null && !t.IsChannel && !t.IsPlaylistCard && seenIds.Add(t.Id))
                                {
                                    list.Add(t);
                                    if (list.Count >= 45) break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                string status = list.Count > 0 ? "OK" : "Não foi possível carregar paradas e lançamentos do YouTube Music.";
                return Tuple.Create(list, status);
            });
        }

        public List<TrackItem> ParseMusicCarouselShelves(string json, HashSet<string> seenIds)
        {
            var results = new List<TrackItem>();
            if (string.IsNullOrEmpty(json)) return results;
            if (seenIds == null) seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string[] shelfBlocks = json.Split(new string[] { "\"musicCarouselShelfRenderer\":" }, StringSplitOptions.None);
                for (int s = 1; s < shelfBlocks.Length; s++)
                {
                    string shelf = shelfBlocks[s];
                    string shelfHeader = ExtractFieldText(shelf, "\"header\"", 800);
                    if (!string.IsNullOrEmpty(shelfHeader))
                    {
                        shelfHeader = UnescapeJsonString(shelfHeader);
                    }

                    ParseMusicRendererBlocksFromSegment(shelf, shelfHeader, seenIds, results);
                    if (results.Count >= 80) break;
                }

                if (results.Count < 10)
                {
                    ParseMusicRendererBlocksFromSegment(json, "", seenIds, results);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("YTMUSIC", "Falha ao analisar prateleiras ParseMusicCarouselShelves: " + ex.Message);
            }

            return results;
        }

        private void ParseMusicRendererBlocksFromSegment(string segment, string shelfCategory, HashSet<string> seenIds, List<TrackItem> results)
        {
            if (string.IsNullOrEmpty(segment)) return;

            // 1. Parse musicResponsiveListItemRenderer (tracks in "Ouvir novamente", "Escolhas rápidas", "Paradas")
            string[] listItems = segment.Split(new string[] { "\"musicResponsiveListItemRenderer\":" }, StringSplitOptions.None);
            for (int i = 1; i < listItems.Length; i++)
            {
                string block = listItems[i];
                if (block.Length > 10000) block = block.Substring(0, 10000);

                string videoId = ExtractRegex(block, "\"videoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                if (string.IsNullOrEmpty(videoId) || seenIds.Contains(videoId)) continue;

                // Extract flexColumns text runs
                var textRuns = Regex.Matches(block, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                string title = "";
                string artist = "";
                string duration = "";

                for (int r = 0; r < textRuns.Count; r++)
                {
                    string val = UnescapeJsonString(textRuns[r].Groups[1].Value).Trim();
                    if (string.IsNullOrEmpty(val) || val == "•" || val == " • " || val == "," || val == " & " || val == "E") continue;

                    if (Regex.IsMatch(val, "^[0-9]{1,2}:[0-9]{2}(:[0-9]{2})?$"))
                    {
                        if (string.IsNullOrEmpty(duration)) duration = val;
                        continue;
                    }

                    if (string.IsNullOrEmpty(title))
                    {
                        title = val;
                    }
                    else if (string.IsNullOrEmpty(artist) &&
                             !val.Equals("Música", StringComparison.OrdinalIgnoreCase) &&
                             !val.Equals("Vídeo", StringComparison.OrdinalIgnoreCase) &&
                             !val.Equals("Song", StringComparison.OrdinalIgnoreCase) &&
                             !val.Equals(title, StringComparison.OrdinalIgnoreCase))
                    {
                        artist = val;
                    }
                }

                if (string.IsNullOrEmpty(title)) continue;
                if (string.IsNullOrEmpty(artist))
                {
                    artist = !string.IsNullOrEmpty(shelfCategory) ? shelfCategory : "YouTube Music";
                }
                else if (!string.IsNullOrEmpty(shelfCategory) &&
                         (shelfCategory.IndexOf("Supermix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          shelfCategory.IndexOf("Ouvir novamente", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    artist = artist + " • " + shelfCategory;
                }

                if (string.IsNullOrEmpty(duration)) duration = "MÚSICA";
                int durSec = ParseDurationToSeconds(duration);

                string thumbUrl = ExtractRegex(block, "\"url\"\\s*:\\s*\"(https://[^\"]+)\"");
                if (!string.IsNullOrEmpty(thumbUrl)) thumbUrl = UnescapeJsonString(thumbUrl);

                seenIds.Add(videoId);
                results.Add(new TrackItem
                {
                    Id = videoId,
                    Title = title,
                    Artist = artist.Replace(" - Topic", ""),
                    Duration = duration,
                    DurationSeconds = durSec,
                    CustomThumbUrl = thumbUrl,
                    ItemType = "video"
                });

                if (results.Count >= 80) return;
            }

            // 2. Parse musicTwoRowItemRenderer (cards in "Ouvir novamente", "Seu Supermix", "Novos lançamentos", carousels)
            string[] twoRowItems = segment.Split(new string[] { "\"musicTwoRowItemRenderer\":" }, StringSplitOptions.None);
            for (int i = 1; i < twoRowItems.Length; i++)
            {
                string block = twoRowItems[i];
                if (block.Length > 10000) block = block.Substring(0, 10000);

                string videoId = ExtractRegex(block, "\"videoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                string playlistId = ExtractRegex(block, "\"playlistId\"\\s*:\\s*\"([a-zA-Z0-9_-]+)\"");

                string title = ExtractFieldText(block, "\"title\"", 600);
                if (string.IsNullOrEmpty(title)) continue;
                title = UnescapeJsonString(title).Trim();

                string subtitle = ExtractFieldText(block, "\"subtitle\"", 700);
                if (!string.IsNullOrEmpty(subtitle)) subtitle = UnescapeJsonString(subtitle).Trim();
                if (string.IsNullOrEmpty(subtitle))
                {
                    subtitle = !string.IsNullOrEmpty(shelfCategory) ? shelfCategory : "YouTube Music";
                }
                else if (!string.IsNullOrEmpty(shelfCategory) && subtitle.IndexOf(shelfCategory, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    subtitle = subtitle + " • " + shelfCategory;
                }

                string thumbUrl = ExtractRegex(block, "\"url\"\\s*:\\s*\"(https://[^\"]+)\"");
                if (!string.IsNullOrEmpty(thumbUrl)) thumbUrl = UnescapeJsonString(thumbUrl);

                if (!string.IsNullOrEmpty(videoId) && !seenIds.Contains(videoId))
                {
                    seenIds.Add(videoId);
                    results.Add(new TrackItem
                    {
                        Id = videoId,
                        PlaylistId = playlistId ?? "",
                        Title = title,
                        Artist = subtitle.Replace(" - Topic", ""),
                        Duration = "MÚSICA",
                        DurationSeconds = 0,
                        CustomThumbUrl = thumbUrl,
                        ItemType = "video"
                    });
                }

                if (results.Count >= 80) return;
            }
        }

        /// <summary>
        /// Robustly extracts text from a YouTube InnerTube field ("title", "lengthText", "ownerText", etc.)
        /// regardless of whether YouTube places "accessibility":{"accessibilityData":{...}} before or after "runs" / "simpleText".
        /// </summary>
        private static string ExtractFieldText(string block, string fieldKey, int maxWindow)
        {
            if (string.IsNullOrEmpty(block) || string.IsNullOrEmpty(fieldKey)) return "";
            int idx = block.IndexOf(fieldKey, StringComparison.Ordinal);
            if (idx < 0) return "";

            int len = Math.Min(maxWindow, block.Length - idx);
            string sub = block.Substring(idx, len);

            // Match whichever comes FIRST immediately inside the field ("runs":[{"text":"..."}], "simpleText":"...", or "content":"...")
            return ExtractRegex(
                sub,
                "(?:\"runs\"\\s*:\\s*\\[\\s*\\{\\s*\"text\"|\"simpleText\"|\"content\")\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""
            );
        }

        private static string ExtractVideoDuration(string block)
        {
            string dur = ExtractFieldText(block, "\"lengthText\"", 450);
            if (string.IsNullOrEmpty(dur))
            {
                dur = ExtractFieldText(block, "\"thumbnailOverlayTimeStatusRenderer\"", 450);
            }
            if (!string.IsNullOrEmpty(dur))
            {
                dur = dur.Trim();
                if (Regex.IsMatch(dur, "^[0-9]+:[0-9]{2}(:[0-9]{2})?$"))
                    return dur;
            }

            // Fallback direct regex for any simpleText timestamp inside the videoRenderer block
            string anyTime = ExtractRegex(block, "\"simpleText\"\\s*:\\s*\"([0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)\"");
            if (!string.IsNullOrEmpty(anyTime)) return anyTime;

            return "";
        }

        /// <summary>
        /// Checks whether a video item is a YouTube Short (using title hashtags, renderer metadata, or platform short indicators).
        /// Ensures Shorts are excluded from the catalog, search results, and channel views.
        /// </summary>
        public static bool IsShortsVideo(string title, int durationSec, string block)
        {
            if (!string.IsNullOrEmpty(title))
            {
                string t = title.ToLowerInvariant();
                if (t.Contains("#shorts") || t.Contains("#short") || t.Contains(" shorts") ||
                    t.StartsWith("shorts ") || t.Contains("#reels") || t.Contains("#reel") ||
                    t.Contains("tiktok") || t.Contains("#tiktok"))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(block))
            {
                if (block.IndexOf("\"reelWatchEndpoint\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    block.IndexOf("\"iconType\":\"YOUTUBE_SHORTS\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    block.IndexOf("\"style\":\"SHORTS\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    block.IndexOf("/shorts/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    block.IndexOf("accessibilityData\":{\"label\":\"Shorts\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // In YouTube, any uploaded video of 58 seconds or less in video mode is classified as a Short
            if (durationSec > 0 && durationSec <= 58)
            {
                return true;
            }

            return false;
        }

        private SearchResultPage ParseInnerTubeSearchPage(string json, bool isVideoMode, bool includeChannelsAndPlaylists)
        {
            var page = new SearchResultPage();
            if (string.IsNullOrEmpty(json)) return page;

            try
            {
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Extract Channel Cards ("channelRenderer") if in initial video search mode
                if (includeChannelsAndPlaylists)
                {
                    string[] chBlocks = json.Split(new string[] { "\"channelRenderer\":" }, StringSplitOptions.None);
                    for (int i = 1; i < chBlocks.Length && i <= 2; i++)
                    {
                        string block = chBlocks[i];
                        int nextVidIdx = block.IndexOf("\"videoRenderer\":", StringComparison.Ordinal);
                        if (nextVidIdx > 0) block = block.Substring(0, nextVidIdx);
                        if (block.Length > 5000) block = block.Substring(0, 5000);

                        string channelId = ExtractRegex(block, "\"channelId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                        if (string.IsNullOrEmpty(channelId) || seenIds.Contains(channelId)) continue;

                        string chTitle = ExtractFieldText(block, "\"title\"", 500);
                        if (string.IsNullOrEmpty(chTitle)) continue;

                        string subText = ExtractFieldText(block, "\"videoCountText\"", 400);
                        if (string.IsNullOrEmpty(subText))
                        {
                            subText = ExtractFieldText(block, "\"subscriberCountText\"", 400);
                        }

                        string thumbUrl = ExtractChannelAvatarUrl(block);
                        if (string.IsNullOrEmpty(thumbUrl))
                        {
                            thumbUrl = ExtractRegex(block, "\"url\"\\s*:\\s*\"((?:https?:)?//[^\"]+)\"");
                            if (!string.IsNullOrEmpty(thumbUrl))
                            {
                                thumbUrl = NormalizeAvatarUrl(thumbUrl);
                            }
                        }

                        string cleanChTitle = UnescapeJsonString(chTitle);
                        if (!string.IsNullOrEmpty(thumbUrl))
                        {
                            CacheChannelAvatarUrl(channelId, cleanChTitle, thumbUrl);
                        }

                        seenIds.Add(channelId);
                        page.Items.Add(new TrackItem
                        {
                            Id = "CH_" + channelId,
                            ChannelId = channelId,
                            Title = cleanChTitle,
                            Artist = !string.IsNullOrEmpty(subText) ? UnescapeJsonString(subText) : "Canal Oficial • Clique para ver vídeos e playlists",
                            Duration = "CANAL",
                            ItemType = "channel",
                            CustomThumbUrl = thumbUrl,
                            ChannelAvatarUrl = thumbUrl,
                            SubscriberCountText = UnescapeJsonString(subText)
                        });
                    }
                }

                // 2. Extract Channel Trailer / Featured Video ("channelVideoPlayerRenderer")
                if (json.IndexOf("\"channelVideoPlayerRenderer\":", StringComparison.Ordinal) >= 0)
                {
                    string trVid = ExtractRegex(json, "\"channelVideoPlayerRenderer\"\\s*:\\s*\\{\\s*\"videoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                    if (!string.IsNullOrEmpty(trVid) && seenIds.Add(trVid))
                    {
                        int trIdx = json.IndexOf("\"channelVideoPlayerRenderer\":", StringComparison.Ordinal);
                        string trBlock = json.Substring(trIdx, Math.Min(3500, json.Length - trIdx));
                        string trTitle = ExtractFieldText(trBlock, "\"title\"", 600);
                        string trDur = ExtractRegex(trBlock, "\"label\"\\s*:\\s*\"[^\"]*?([0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)[^\"]*\"");
                        if (string.IsNullOrEmpty(trDur)) trDur = "DESTAQUE";
                        int trDurSec = ParseDurationToSeconds(trDur);
                        if (!string.IsNullOrEmpty(trTitle))
                        {
                            page.Items.Add(new TrackItem
                            {
                                Id = trVid,
                                Title = UnescapeJsonString(trTitle),
                                Artist = "Destaque do Canal",
                                Duration = trDur,
                                DurationSeconds = trDurSec,
                                ItemType = "video"
                            });
                        }
                    }
                }

                // 3. Extract Video Cards ("videoRenderer", "gridVideoRenderer", "playlistVideoRenderer", "compactVideoRenderer")
                string[] videoSplitKeys = new string[] { "\"videoRenderer\":", "\"gridVideoRenderer\":", "\"playlistVideoRenderer\":", "\"compactVideoRenderer\":" };
                string[] blocks = json.Split(videoSplitKeys, StringSplitOptions.None);
                for (int i = 1; i < blocks.Length; i++)
                {
                    string block = blocks[i];
                    if (block.Length > 14000) block = block.Substring(0, 14000);

                    string videoId = ExtractRegex(block, "\"videoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                    if (string.IsNullOrEmpty(videoId) || seenIds.Contains(videoId)) continue;

                    string title = ExtractFieldText(block, "\"title\"", 750);
                    if (string.IsNullOrEmpty(title)) continue;

                    string duration = ExtractVideoDuration(block);
                    if (string.IsNullOrEmpty(duration))
                    {
                        // In music mode, skip live streams without duration; in video mode allow with badge
                        if (!isVideoMode) continue;
                        duration = "VÍDEO";
                    }

                    string artist = ExtractFieldText(block, "\"ownerText\"", 600);
                    if (string.IsNullOrEmpty(artist))
                    {
                        artist = ExtractFieldText(block, "\"longBylineText\"", 600);
                    }
                    if (string.IsNullOrEmpty(artist))
                    {
                        artist = ExtractFieldText(block, "\"shortBylineText\"", 600);
                    }
                    if (string.IsNullOrEmpty(artist)) artist = "YouTube";

                    string channelId = ExtractRegex(block, "\"browseId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                    string thumbUrl = ExtractRegex(block, "\"url\"\\s*:\\s*\"((?:https?:)?//[^\"]*?ytimg\\.com/[^\"]+)\"");
                    if (string.IsNullOrEmpty(thumbUrl))
                    {
                        thumbUrl = ExtractRegex(block, "\"url\"\\s*:\\s*\"((?:https?:)?//[^\"]*?ggpht\\.com/[^\"]+)\"");
                    }
                    if (!string.IsNullOrEmpty(thumbUrl))
                    {
                        thumbUrl = UnescapeJsonString(thumbUrl).Replace("\\/", "/");
                        if (thumbUrl.StartsWith("//")) thumbUrl = "https:" + thumbUrl;
                    }

                    string channelAvatarUrl = ExtractChannelAvatarUrl(block);

                    string viewCountText = ExtractFieldText(block, "\"viewCountText\"", 300);
                    if (string.IsNullOrEmpty(viewCountText))
                    {
                        viewCountText = ExtractFieldText(block, "\"shortViewCountText\"", 300);
                    }
                    string publishedTimeText = ExtractFieldText(block, "\"publishedTimeText\"", 300);

                    title = UnescapeJsonString(title);
                    artist = UnescapeJsonString(artist).Replace(" - Topic", "");
                    viewCountText = UnescapeJsonString(viewCountText);
                    publishedTimeText = UnescapeJsonString(publishedTimeText);
                    int durationSec = ParseDurationToSeconds(duration);

                    if (!string.IsNullOrEmpty(channelAvatarUrl))
                    {
                        CacheChannelAvatarUrl(channelId, artist, channelAvatarUrl);
                    }
                    else
                    {
                        channelAvatarUrl = GetCachedChannelAvatarUrl(channelId, artist);
                    }

                    if (isVideoMode && IsShortsVideo(title, durationSec, block))
                        continue;

                    seenIds.Add(videoId);
                    page.Items.Add(new TrackItem
                    {
                        Id = videoId,
                        Title = title,
                        Artist = artist,
                        Duration = duration,
                        DurationSeconds = durationSec,
                        ChannelId = channelId,
                        CustomThumbUrl = thumbUrl,
                        ChannelAvatarUrl = channelAvatarUrl,
                        ViewCountText = viewCountText,
                        PublishedTimeText = publishedTimeText,
                        ItemType = "video",
                        IsAlbum = !isVideoMode && durationSec >= 1200
                    });

                    if (page.Items.Count >= 60) break;
                }

                // 4. Extract Modern Lockup Cards ("lockupViewModel")
                if (page.Items.Count < 50 && json.IndexOf("\"lockupViewModel\":", StringComparison.Ordinal) >= 0)
                {
                    string[] lockBlocks = json.Split(new string[] { "\"lockupViewModel\":" }, StringSplitOptions.None);
                    for (int i = 1; i < lockBlocks.Length; i++)
                    {
                        string b = lockBlocks[i];
                        if (b.Length > 8000) b = b.Substring(0, 8000);
                        if (b.IndexOf("LOCKUP_CONTENT_TYPE_PLAYLIST", StringComparison.Ordinal) >= 0) continue;

                        string vidId = ExtractRegex(b, "\"animationActivationTargetId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                        if (string.IsNullOrEmpty(vidId))
                        {
                            vidId = ExtractRegex(b, "/vi(?:_lc)?/([a-zA-Z0-9_-]{11})/");
                        }
                        if (string.IsNullOrEmpty(vidId))
                        {
                            vidId = ExtractRegex(b, "\"videoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                        }
                        if (string.IsNullOrEmpty(vidId))
                        {
                            vidId = ExtractRegex(b, "\"addedVideoId\"\\s*:\\s*\"([a-zA-Z0-9_-]{11})\"");
                        }

                        if (string.IsNullOrEmpty(vidId) || seenIds.Contains(vidId)) continue;

                        string title = ExtractRegex(b, "\"title\"\\s*:\\s*\\{\\s*\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (string.IsNullOrEmpty(title))
                        {
                            title = ExtractFieldText(b, "\"title\"", 600);
                        }
                        if (string.IsNullOrEmpty(title)) continue;

                        string dur = ExtractRegex(b, "\"text\"\\s*:\\s*\"([0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)\"");
                        if (string.IsNullOrEmpty(dur))
                        {
                            dur = ExtractRegex(b, "\"label\"\\s*:\\s*\"[^\"]*?([0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)[^\"]*\"");
                        }
                        if (string.IsNullOrEmpty(dur)) dur = "VÍDEO";

                        int durationSec = ParseDurationToSeconds(dur);
                        if (isVideoMode && IsShortsVideo(title, durationSec, b)) continue;

                        string artist = ExtractRegex(b, "\"a11yLabel\"\\s*:\\s*\"(?:Acessar o canal|Go to channel)\\s+((?:[^\"\\\\]|\\\\.)*)\"");
                        if (string.IsNullOrEmpty(artist))
                        {
                            artist = ExtractRegex(b, "\"metadataParts\"\\s*:\\s*\\[\\s*\\{\\s*\"text\"\\s*:\\s*\\{\\s*\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        }
                        if (string.IsNullOrEmpty(artist))
                        {
                            artist = ExtractFieldText(b, "\"ownerText\"", 600);
                        }
                        if (string.IsNullOrEmpty(artist))
                        {
                            artist = ExtractFieldText(b, "\"shortBylineText\"", 600);
                        }
                        if (string.IsNullOrEmpty(artist)) artist = "YouTube";

                        string chId = ExtractRegex(b, "\"browseId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                        string thumbUrl = ExtractRegex(b, "\"url\"\\s*:\\s*\"((?:https?:)?//i\\.ytimg\\.com/[^\"]+)\"");
                        if (!string.IsNullOrEmpty(thumbUrl))
                        {
                            thumbUrl = UnescapeJsonString(thumbUrl).Replace("\\/", "/");
                            if (thumbUrl.StartsWith("//")) thumbUrl = "https:" + thumbUrl;
                        }

                        string channelAvatarUrl = ExtractChannelAvatarUrl(b);

                        title = UnescapeJsonString(title);
                        artist = UnescapeJsonString(artist).Replace(" - Topic", "");

                        if (!string.IsNullOrEmpty(channelAvatarUrl))
                        {
                            CacheChannelAvatarUrl(chId, artist, channelAvatarUrl);
                        }
                        else
                        {
                            channelAvatarUrl = GetCachedChannelAvatarUrl(chId, artist);
                        }

                        seenIds.Add(vidId);
                        page.Items.Add(new TrackItem
                        {
                            Id = vidId,
                            Title = title,
                            Artist = artist,
                            Duration = dur,
                            DurationSeconds = durationSec,
                            ChannelId = chId,
                            CustomThumbUrl = thumbUrl,
                            ChannelAvatarUrl = channelAvatarUrl,
                            ItemType = "video",
                            IsAlbum = !isVideoMode && durationSec >= 1200
                        });

                        if (page.Items.Count >= 60) break;
                    }
                }

                // 5. Extract Continuation Token for Infinite Scroll
                var contMatches = Regex.Matches(json, "\"continuationCommand\"\\s*:\\s*\\{\\s*\"token\"\\s*:\\s*\"([^\"]+)\"");
                if (contMatches.Count > 0)
                {
                    page.ContinuationToken = UnescapeJsonString(contMatches[contMatches.Count - 1].Groups[1].Value);
                }
            }
            catch { }

            return page;
        }

        private List<TrackItem> ParsePlaylistsFromInnerTubeJson(string json, string defaultChannelName, string defaultChannelId, bool strictChannelMatch)
        {
            var list = new List<TrackItem>();
            if (string.IsNullOrEmpty(json)) return list;

            var seenPl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Parse classic playlistRenderer & gridPlaylistRenderer
                string[] splitKeys = new string[] { "\"playlistRenderer\":", "\"gridPlaylistRenderer\":" };
                string[] blocks = json.Split(splitKeys, StringSplitOptions.None);
                for (int i = 1; i < blocks.Length; i++)
                {
                    string b = blocks[i];
                    if (b.Length > 8000) b = b.Substring(0, 8000);

                    string plId = ExtractRegex(b, "\"playlistId\"\\s*:\\s*\"([a-zA-Z0-9_-]+)\"");
                    if (string.IsNullOrEmpty(plId) || seenPl.Contains(plId)) continue;

                    if (strictChannelMatch && !string.IsNullOrEmpty(defaultChannelId))
                    {
                        string ownerBrowseId = ExtractRegex(b, "\"browseId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                        if (!string.IsNullOrEmpty(ownerBrowseId) && !string.Equals(ownerBrowseId, defaultChannelId, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    string title = ExtractFieldText(b, "\"title\"", 650);
                    if (string.IsNullOrEmpty(title)) continue;

                    string countText = ExtractFieldText(b, "\"videoCountShortText\"", 350);
                    if (string.IsNullOrEmpty(countText))
                    {
                        countText = ExtractFieldText(b, "\"videoCountText\"", 350);
                    }
                    if (string.IsNullOrEmpty(countText))
                    {
                        countText = ExtractRegex(b, "\"videoCount\"\\s*:\\s*\"([0-9]+)\"");
                    }

                    string coverVidId = ExtractRegex(b, "/vi/([a-zA-Z0-9_-]{11})/");
                    string thumbUrl = !string.IsNullOrEmpty(coverVidId)
                        ? ("https://i.ytimg.com/vi/" + coverVidId + "/mqdefault.jpg")
                        : ExtractRegex(b, "\"url\"\\s*:\\s*\"(https://i\\.ytimg\\.com/[^\"]+)\"");

                    seenPl.Add(plId);
                    list.Add(new TrackItem
                    {
                        Id = !string.IsNullOrEmpty(coverVidId) ? coverVidId : ("PL_" + plId),
                        PlaylistId = plId,
                        ChannelId = defaultChannelId,
                        Title = UnescapeJsonString(title),
                        Artist = defaultChannelName + (!string.IsNullOrEmpty(countText) ? (" • " + UnescapeJsonString(countText)) : " • Playlist"),
                        Duration = "PLAYLIST",
                        ItemType = "playlist",
                        CustomThumbUrl = UnescapeJsonString(thumbUrl)
                    });
                }

                // 2. Parse newer YouTube lockupViewModel playlists
                string[] lockupBlocks = json.Split(new string[] { "\"lockupViewModel\":" }, StringSplitOptions.None);
                for (int i = 1; i < lockupBlocks.Length; i++)
                {
                    string b = lockupBlocks[i];
                    if (b.Length > 8000) b = b.Substring(0, 8000);

                    string plId = ExtractRegex(b, "\"contentId\"\\s*:\\s*\"(PL[a-zA-Z0-9_-]+|OL[a-zA-Z0-9_-]+|UU[a-zA-Z0-9_-]+)\"");
                    if (string.IsNullOrEmpty(plId))
                    {
                        plId = ExtractRegex(b, "\"playlistId\"\\s*:\\s*\"([a-zA-Z0-9_-]+)\"");
                    }
                    if (string.IsNullOrEmpty(plId) || seenPl.Contains(plId)) continue;

                    if (strictChannelMatch && !string.IsNullOrEmpty(defaultChannelId))
                    {
                        string ownerBrowseId = ExtractRegex(b, "\"browseId\"\\s*:\\s*\"(UC[a-zA-Z0-9_-]+)\"");
                        if (!string.IsNullOrEmpty(ownerBrowseId) && !string.Equals(ownerBrowseId, defaultChannelId, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    string title = ExtractFieldText(b, "\"title\"", 650);
                    if (string.IsNullOrEmpty(title)) continue;

                    string coverVidId = ExtractRegex(b, "/vi/([a-zA-Z0-9_-]{11})/");
                    string thumbUrl = !string.IsNullOrEmpty(coverVidId)
                        ? ("https://i.ytimg.com/vi/" + coverVidId + "/mqdefault.jpg")
                        : ExtractRegex(b, "\"url\"\\s*:\\s*\"(https://i\\.ytimg\\.com/[^\"]+)\"");

                    seenPl.Add(plId);
                    list.Add(new TrackItem
                    {
                        Id = !string.IsNullOrEmpty(coverVidId) ? coverVidId : ("PL_" + plId),
                        PlaylistId = plId,
                        ChannelId = defaultChannelId,
                        Title = UnescapeJsonString(title),
                        Artist = defaultChannelName + " • Playlist do Canal",
                        Duration = "PLAYLIST",
                        ItemType = "playlist",
                        CustomThumbUrl = UnescapeJsonString(thumbUrl)
                    });
                }
            }
            catch { }

            return list;
        }

        private List<TrackItem> FallbackYtDlpSearch(string query, int count)
        {
            var list = new List<TrackItem>();
            try
            {
                string args = string.Format("ytsearch{0}:\"{1}\" --flat-playlist --print \"%(id)s\t%(title)s\t%(uploader)s\t%(duration_string)s\" --no-warnings", count, query.Replace("\"", ""));
                string output = RunYtDlpProcess(args, 15000);
                if (!string.IsNullOrEmpty(output))
                {
                    list = ParseFlatPlaylistOutput(output);
                }
            }
            catch { }
            return list;
        }

        public static string NormalizePlaylistUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            input = input.Trim();

            if (!input.Contains("/") && !input.Contains(".") && input.Length >= 12)
            {
                return "https://www.youtube.com/playlist?list=" + input;
            }

            var m = Regex.Match(input, "[?&]list=([a-zA-Z0-9_-]+)");
            if (m.Success)
            {
                string listId = m.Groups[1].Value;
                if (listId.Equals("LM", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://music.youtube.com/playlist?list=LM";
                }
                return "https://www.youtube.com/playlist?list=" + listId;
            }

            return input;
        }

        public async Task<Tuple<List<TrackItem>, string>> FetchUrlOrPlaylistAsync(string urlOrTarget, AppConfig config, bool requireBrowserCookies, int startIndex = 1, int maxItems = 60)
        {
            return await Task.Run(() =>
            {
                var list = new List<TrackItem>();
                if (!File.Exists(_ytDlpPath))
                {
                    return Tuple.Create(list, "O arquivo bin\\yt-dlp.exe não foi encontrado.");
                }

                string normalizedTarget = NormalizePlaylistUrl(urlOrTarget);
                string stdErr = "";
                string output = "";

                bool isAccountFeed = (normalizedTarget.TrimEnd('/') == "https://www.youtube.com" ||
                                      normalizedTarget.IndexOf("feed/recommended", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      normalizedTarget.IndexOf("feed/subscriptions", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      normalizedTarget.IndexOf("feed/history", StringComparison.OrdinalIgnoreCase) >= 0);

                int endIndex = startIndex + Math.Max(10, maxItems) - 1;

                if (!requireBrowserCookies && !isAccountFeed)
                {
                    string publicArgs = string.Format(
                        "--yes-playlist --flat-playlist --playlist-start {0} --playlist-end {1} --extractor-args \"youtubetab:skip=authcheck\" --print \"%(id)s\t%(title)s\t%(uploader)s\t%(duration_string)s\t%(channel_id)s\" --no-warnings \"{2}\"",
                        startIndex,
                        endIndex,
                        normalizedTarget.Replace("\"", "")
                    );
                    output = RunYtDlpProcessWithError(publicArgs, 35000, out stdErr);
                    list = ParseFlatPlaylistOutput(output);
                    if (list.Count > 0)
                    {
                        return Tuple.Create(list, "OK");
                    }
                }

                using var cookieScope = CookieVault.CreateYtDlpCookieScope(config?.CookiesFilePath, config?.BrowserForCookies);
                string cookieArg = cookieScope.Arg;

                if (!string.IsNullOrEmpty(cookieArg) || requireBrowserCookies || isAccountFeed)
                {
                    string args = string.Format(
                        "{0}--yes-playlist --flat-playlist --playlist-start {1} --playlist-end {2} --extractor-args \"youtubetab:skip=authcheck\" --print \"%(id)s\t%(title)s\t%(uploader)s\t%(duration_string)s\t%(channel_id)s\" --no-warnings \"{3}\"",
                        cookieArg,
                        startIndex,
                        endIndex,
                        normalizedTarget.Replace("\"", "")
                    );

                    output = RunYtDlpProcessWithError(args, 45000, out stdErr);
                    list = ParseFlatPlaylistOutput(output);
                }

                if (list.Count == 0 && !string.IsNullOrEmpty(stdErr))
                {
                    if (stdErr.IndexOf("Could not copy Chrome cookie database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdErr.IndexOf("PermissionError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdErr.IndexOf("Failed to decrypt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return Tuple.Create(list,
                            "Para feeds e playlists da conta, o navegador (" + config.BrowserForCookies.ToUpper() + ") bloqueia os cookies enquanto está aberto.\n\n" +
                            "Solução rápida (escolha uma):\n" +
                            "1. Feche o navegador por 3 segundos enquanto carrega os recomendados.\n" +
                            "2. Ou exporte os cookies para um arquivo cookies.txt nas configurações.");
                    }
                    return Tuple.Create(list, "Não foi possível carregar os vídeos do feed. Verifique sua conexão ou tente novamente.");
                }

                return Tuple.Create(list, list.Count > 0 ? "OK" : "Nenhuma faixa encontrada.");
            });
        }

        private List<TrackItem> ParseFlatPlaylistOutput(string output)
        {
            var list = new List<TrackItem>();
            if (string.IsNullOrEmpty(output)) return list;

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    string id = parts[0].Trim();
                    string title = parts[1].Trim();
                    if (title == "[Private video]" || title == "[Deleted video]") continue;

                    // Exclude Mixes, Radio, and invalid playlist IDs
                    if (id.Length != 11 || id.StartsWith("RD") || id.StartsWith("PL") || id.StartsWith("VL")) continue;
                    if (title.StartsWith("Mix -", StringComparison.OrdinalIgnoreCase)) continue;

                    string artist = (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "NA")
                        ? parts[2].Replace(" - Topic", "").Trim()
                        : "YouTube";
                    string dur = (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]) && parts[3] != "NA")
                        ? parts[3].Trim()
                        : "--:--";
                    string chId = (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]) && parts[4] != "NA")
                        ? parts[4].Trim()
                        : "";

                    int durSec = ParseDurationToSeconds(dur);
                    if (IsShortsVideo(title, durSec, "")) continue;

                    list.Add(new TrackItem
                    {
                        Id = id,
                        Title = title,
                        Artist = artist,
                        Duration = dur,
                        DurationSeconds = durSec,
                        ChannelId = chId,
                        ChannelAvatarUrl = GetCachedChannelAvatarUrl(chId, artist),
                        ItemType = "video"
                    });
                }
            }
            return list;
        }

        public void InvalidateCachedTrack(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;
            string safeId = Regex.Replace(videoId, "[^a-zA-Z0-9_-]", "");
            string cachedFile = Path.Combine(_cacheDir, safeId + ".m4a");
            TryDeleteFile(cachedFile);
        }

        private void EnsureStandardM4aContainer(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            if (!File.Exists(_ffmpegPath)) return;

            try
            {
                bool isDashFragmented = false;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length >= 16)
                    {
                        byte[] hdr = new byte[16];
                        fs.Read(hdr, 0, 16);
                        string brand = Encoding.ASCII.GetString(hdr, 4, 8);
                        if (brand.Equals("ftypdash", StringComparison.OrdinalIgnoreCase))
                        {
                            isDashFragmented = true;
                        }
                    }
                }

                if (!isDashFragmented) return;

                string remuxedTemp = filePath + ".remux.m4a";
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = string.Format("-y -loglevel error -i \"{0}\" -c copy -movflags +faststart \"{1}\"", filePath, remuxedTemp),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null) proc.WaitForExit(12000);
                }

                if (File.Exists(remuxedTemp) && new FileInfo(remuxedTemp).Length > 10240)
                {
                    lock (_cacheLock)
                    {
                        File.Delete(filePath);
                        File.Move(remuxedTemp, filePath);
                    }
                }
                else
                {
                    TryDeleteFile(remuxedTemp);
                }
            }
            catch { }
        }

        private string RunYtDlpFastUrl(string arguments, int timeoutMs)
        {
            string err;
            return RunYtDlpFastUrl(arguments, timeoutMs, out err);
        }

        private string RunYtDlpFastUrl(string arguments, int timeoutMs, out string stdErr)
        {
            string outStr = RunYtDlpProcessWithError(arguments, timeoutMs, out stdErr);
            if (!string.IsNullOrEmpty(outStr))
            {
                string[] lines = outStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i].Trim();
                    if (l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return l;
                    }
                }
            }
            return null;
        }

        private static bool IsVideoUnavailableError(string stdErr)
        {
            if (string.IsNullOrEmpty(stdErr)) return false;
            return stdErr.IndexOf("Video unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   stdErr.IndexOf("This video is not available", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   stdErr.IndexOf("Private video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   stdErr.IndexOf("This video has been removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   stdErr.IndexOf("Account terminated", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string TryResolveReplacementVideoId(TrackItem track, AppConfig config)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.Title)) return null;

            string query = track.Title.Trim();
            if (!string.IsNullOrWhiteSpace(track.Artist) &&
                !track.Artist.Equals("YouTube", StringComparison.OrdinalIgnoreCase) &&
                query.IndexOf(track.Artist.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                query = track.Artist.Trim() + " - " + query;
            }

            var candidates = new List<TrackItem>();
            try
            {
                string escapedQuery = EscapeJson(query);
                string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"query\":\"" + escapedQuery + "\"}";
                string jsonResponse = PostInnerTube("https://www.youtube.com/youtubei/v1/search?prettyPrint=false", payload, config);
                var page = ParseInnerTubeSearchPage(jsonResponse, false, false);
                if (page != null && page.Items != null)
                {
                    candidates.AddRange(page.Items);
                }
            }
            catch { }

            if (candidates.Count == 0 && File.Exists(_ytDlpPath))
            {
                try
                {
                    candidates.AddRange(FallbackYtDlpSearch(query, 6));
                }
                catch { }
            }

            string fallbackId = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsChannel || c.IsPlaylistCard) continue;
                if (string.IsNullOrWhiteSpace(c.Id) || string.Equals(c.Id, track.Id, StringComparison.OrdinalIgnoreCase)) continue;

                if (fallbackId == null) fallbackId = c.Id;

                if (track.DurationSeconds > 0 && c.DurationSeconds > 0)
                {
                    if (Math.Abs(c.DurationSeconds - track.DurationSeconds) <= 60)
                    {
                        return c.Id;
                    }
                }
                else if (track.DurationSeconds <= 0)
                {
                    return c.Id;
                }
            }

            return fallbackId;
        }

        private static void HealTrackIdInConfig(AppConfig config, string oldId, string newId)
        {
            if (config == null || string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId)) return;
            bool changed = false;
            try
            {
                if (config.Playlists != null)
                {
                    for (int p = 0; p < config.Playlists.Count; p++)
                    {
                        var pl = config.Playlists[p];
                        if (pl == null || pl.Tracks == null) continue;
                        for (int t = 0; t < pl.Tracks.Count; t++)
                        {
                            if (pl.Tracks[t] != null && string.Equals(pl.Tracks[t].Id, oldId, StringComparison.OrdinalIgnoreCase))
                            {
                                pl.Tracks[t].Id = newId;
                                changed = true;
                            }
                        }
                    }
                }
                if (config.Favorites != null)
                {
                    for (int f = 0; f < config.Favorites.Count; f++)
                    {
                        if (config.Favorites[f] != null && string.Equals(config.Favorites[f].Id, oldId, StringComparison.OrdinalIgnoreCase))
                        {
                            config.Favorites[f].Id = newId;
                            changed = true;
                        }
                    }
                }
                if (changed)
                {
                    config.Save();
                }
            }
            catch { }
        }

        /// <summary>
        /// Resolves a playable local cache file (.m4a) for the Music Environment.
        /// Uses ultra-fast VisionOS direct URL extraction (~2s) and unthrottled ffmpeg download (~250ms),
        /// completely preventing the 40s timeouts and rate-limit drops that previously caused music to stop!
        /// </summary>
        public async Task<string> GetPlayableSourceAsync(TrackItem track, AppConfig config, CancellationToken token)
        {
            if (track == null || string.IsNullOrEmpty(track.Id)) return null;

            string safeId = Regex.Replace(track.Id, "[^a-zA-Z0-9_-]", "");
            string cachedFile = Path.Combine(_cacheDir, safeId + ".m4a");

            if (File.Exists(cachedFile))
            {
                var info = new FileInfo(cachedFile);
                if (info.Length > 10240)
                {
                    try { File.SetLastWriteTimeUtc(cachedFile, DateTime.UtcNow); } catch { }
                    Logger.Info("MUSIC", string.Format("Reproduzindo do cache local: Id={0} ('{1}')", track.Id, track.Title));
                    return cachedFile;
                }
            }

            if (!string.IsNullOrEmpty(track.Id) && _audioUrlMemoryCache.TryGetValue(track.Id, out var cachedMem))
            {
                if (!string.IsNullOrEmpty(cachedMem.Url) && cachedMem.ExpiresAtUtc > DateTime.UtcNow)
                {
                    Logger.Info("PREFETCH", string.Format("Stream de áudio pré-resolvido em RAM reutilizado para Id={0} ('{1}')", track.Id, track.Title));
                    return cachedMem.Url;
                }
                _audioUrlMemoryCache.TryRemove(track.Id, out _);
            }

            int maxCacheMB = (config != null && config.MaxCacheSizeMB >= 15) ? config.MaxCacheSizeMB : 100;

            return await Task.Run(() =>
            {
                if (token.IsCancellationRequested) return null;

                Logger.Info("MUSIC", string.Format("Resolvendo stream de áudio: Id={0}, Title='{1}', Artist='{2}'", track.Id, track.Title, track.Artist));

                string videoUrl = "https://www.youtube.com/watch?v=" + track.Id;
                using var cookieScope = CookieVault.CreateYtDlpCookieScope(config?.CookiesFilePath, config?.BrowserForCookies);
                string cookieArg = cookieScope.Arg;
                string denoPath = Path.Combine(_binDir, "deno.exe");
                string jsArg = File.Exists(denoPath) ? string.Format("--js-runtimes \"deno:{0}\" ", denoPath) : "";
                string formatSelector = "bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio";

                // Step 1: Extração rápida via yt-dlp com cookies e Deno JS runtime (pipeline padrão estável)
                string attempt1Args = string.Format(
                    "{0}{1}--no-playlist --no-warnings --extractor-args \"youtube:skip=hls,translated_subs\" -f \"{2}\" -g \"{3}\"",
                    !string.IsNullOrEmpty(cookieArg) ? cookieArg : "",
                    jsArg,
                    formatSelector,
                    videoUrl
                );
                string stdErr1;
                string directUrl = RunYtDlpFastUrl(attempt1Args, 15000, out stdErr1);

                // Auto-recuperação imediata quando o ID salvo na playlist foi derrubado/substituído no YouTube ("Video unavailable")
                if (string.IsNullOrEmpty(directUrl) && IsVideoUnavailableError(stdErr1) && !token.IsCancellationRequested)
                {
                    Logger.Warn("MUSIC", string.Format("Vídeo original indisponível no YouTube (Id={0}, '{1} - {2}'). Buscando versão ativa equivalente...", track.Id, track.Artist, track.Title));
                    string replacementId = TryResolveReplacementVideoId(track, config);
                    if (!string.IsNullOrEmpty(replacementId) && !string.Equals(replacementId, track.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        string oldId = track.Id;
                        track.Id = replacementId;
                        safeId = Regex.Replace(replacementId, "[^a-zA-Z0-9_-]", "");
                        cachedFile = Path.Combine(_cacheDir, safeId + ".m4a");
                        videoUrl = "https://www.youtube.com/watch?v=" + replacementId;

                        HealTrackIdInConfig(config, oldId, replacementId);
                        Logger.Info("MUSIC", string.Format("Faixa auto-reparada na playlist: '{0}' ({1} -> {2})", track.Title, oldId, replacementId));

                        if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 10240)
                        {
                            try { File.SetLastWriteTimeUtc(cachedFile, DateTime.UtcNow); } catch { }
                            return cachedFile;
                        }

                        attempt1Args = string.Format(
                            "{0}{1}--no-playlist --no-warnings --extractor-args \"youtube:skip=hls,translated_subs\" -f \"{2}\" -g \"{3}\"",
                            !string.IsNullOrEmpty(cookieArg) ? cookieArg : "",
                            jsArg,
                            formatSelector,
                            videoUrl
                        );
                        directUrl = RunYtDlpFastUrl(attempt1Args, 15000, out stdErr1);
                    }
                    else
                    {
                        Logger.Error("MUSIC", string.Format("Falha ao localizar substituto ativo para faixa indisponível: Id={0} ('{1}')", track.Id, track.Title));
                        return null;
                    }
                }

                // Step 2: Fallback sem cookies
                if (string.IsNullOrEmpty(directUrl) && !token.IsCancellationRequested)
                {
                    string attempt2Args = string.Format(
                        "{0}--no-playlist --no-warnings --extractor-args \"youtube:skip=hls,translated_subs\" -f \"{1}\" -g \"{2}\"",
                        jsArg,
                        formatSelector,
                        videoUrl
                    );
                    directUrl = RunYtDlpFastUrl(attempt2Args, 15000);
                }

                // Step 3: Fallback via client visionos
                if (string.IsNullOrEmpty(directUrl) && !token.IsCancellationRequested)
                {
                    directUrl = RunYtDlpFastUrl(string.Format("--no-playlist --no-warnings --extractor-args \"youtube:player_client=visionos;skip=hls,translated_subs\" -f \"{0}\" -g \"{1}\"", formatSelector, videoUrl), 8000);
                }

                // If directUrl resolved, cache in RAM and return directly for instant zero-disk streaming!
                if (!string.IsNullOrEmpty(directUrl))
                {
                    CacheResolvedAudioUrl(track.Id, directUrl);
                    Logger.Info("MUSIC", string.Format("Stream direto resolvido com sucesso para Id={0} ('{1}')", track.Id, track.Title));
                    return directUrl;
                }

                // Step 5: Direct yt-dlp file download fallback if URL extraction was empty
                if (track.DurationSeconds <= 1800 && !token.IsCancellationRequested)
                {
                    string tempFile = Path.Combine(_cacheDir, safeId + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".m4a");
                    string ffmpegArg = (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath))
                        ? string.Format("--ffmpeg-location \"{0}\" --fixup force ", Path.GetDirectoryName(_ffmpegPath))
                        : "";
                    string dlArgs = string.Format(
                        "{0}{1}--no-playlist -f \"{2}\" --no-part --no-warnings -o \"{3}\" \"{4}\"",
                        ffmpegArg,
                        cookieArg,
                        formatSelector,
                        tempFile,
                        videoUrl
                    );

                    RunYtDlpProcess(dlArgs, 30000);

                    if (token.IsCancellationRequested)
                    {
                        TryDeleteFile(tempFile);
                        return null;
                    }

                    if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 10240)
                    {
                        EnsureStandardM4aContainer(tempFile);
                        lock (_cacheLock)
                        {
                            try
                            {
                                if (File.Exists(cachedFile)) File.Delete(cachedFile);
                                File.Move(tempFile, cachedFile);
                                File.SetLastWriteTimeUtc(cachedFile, DateTime.UtcNow);
                            }
                            catch
                            {
                                return tempFile;
                            }
                        }
                        EnforceCacheSizeLimit(maxCacheMB);
                        Logger.Info("MUSIC", string.Format("Download fallback concluído para Id={0} ('{1}')", track.Id, track.Title));
                        return cachedFile;
                    }
                    TryDeleteFile(tempFile);
                }

                if (!string.IsNullOrEmpty(directUrl)) return directUrl;
                Logger.Error("MUSIC", string.Format("Não foi possível resolver áudio para Id={0} ('{1}') após todas as tentativas.", track.Id, track.Title));
                return null;
            }, token);
        }

        private static void CacheResolvedAudioUrl(string videoId, string url)
        {
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(url)) return;
            DateTime now = DateTime.UtcNow;
            if (_audioUrlMemoryCache.Count >= 64)
            {
                foreach (var kv in _audioUrlMemoryCache)
                {
                    if (kv.Value.ExpiresAtUtc <= now)
                    {
                        _audioUrlMemoryCache.TryRemove(kv.Key, out _);
                    }
                }
                if (_audioUrlMemoryCache.Count >= 64)
                {
                    _audioUrlMemoryCache.Clear();
                }
            }
            _audioUrlMemoryCache[videoId] = (url, now.AddHours(2));
        }

        public void PrefetchTrackInBackground(TrackItem nextTrack, AppConfig config)
        {
            if (nextTrack == null || string.IsNullOrEmpty(nextTrack.Id)) return;
            if (nextTrack.DurationSeconds > 900) return;

            string safeId = Regex.Replace(nextTrack.Id, "[^a-zA-Z0-9_-]", "");
            string cachedFile = Path.Combine(_cacheDir, safeId + ".m4a");
            if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 10240) return;

            Task.Run(async () =>
            {
                try
                {
                    await GetPlayableSourceAsync(nextTrack, config, CancellationToken.None);
                    MemoryOptimizer.TrimMemory();
                }
                catch { }
            });
        }

        public void EnforceCacheSizeLimit(int maxMegabytes)
        {
            EnforceDirectorySizeLimit(_cacheDir, "*.m4a", maxMegabytes < 15 ? 100 : maxMegabytes);
        }

        private void EnforceDirectorySizeLimit(string directoryPath, string pattern, int maxMegabytes)
        {
            long maxBytes = (long)maxMegabytes * 1024L * 1024L;
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                if (!dirInfo.Exists) return;

                foreach (var tmp in dirInfo.GetFiles("*.part"))
                {
                    TryDeleteFile(tmp.FullName);
                }

                FileInfo[] files = dirInfo.GetFiles(pattern);
                long totalBytes = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    totalBytes += files[i].Length;
                }

                if (totalBytes <= maxBytes) return;

                // Sort newest first so we delete oldest from the end, protecting at least the newest 5 files
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                long targetBytes = (long)(maxBytes * 0.80); // Trim to 80% of cap
                for (int i = files.Length - 1; i >= 5 && totalBytes > targetBytes; i--)
                {
                    long len = files[i].Length;
                    if (TryDeleteFile(files[i].FullName))
                    {
                        totalBytes -= len;
                    }
                }
            }
            catch { }
        }

        private bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private string RunYtDlpProcess(string arguments, int timeoutMs)
        {
            string err;
            return RunYtDlpProcessWithError(arguments, timeoutMs, out err);
        }

        private string RunYtDlpProcessWithError(string arguments, int timeoutMs, out string stdError)
        {
            stdError = "";
            if (!File.Exists(_ytDlpPath)) return "";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = "--encoding utf-8 " + arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";

                using (var proc = new Process { StartInfo = psi })
                {
                    var sbOut = new StringBuilder();
                    var sbErr = new StringBuilder();

                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                    }
                    else
                    {
                        proc.WaitForExit();
                    }

                    stdError = sbErr.ToString();
                    return sbOut.ToString();
                }
            }
            catch (Exception ex)
            {
                stdError = ex.Message;
                return "";
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ExtractRegex(string input, string pattern)
        {
            var m = Regex.Match(input, pattern);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string UnescapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            try
            {
                return Regex.Unescape(str);
            }
            catch
            {
                return str.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\u0026", "&");
            }
        }

        public static int ParseDurationToSeconds(string dur)
        {
            if (string.IsNullOrWhiteSpace(dur) || dur == "--:--") return 0;
            try
            {
                string trimmed = dur.Trim();
                if (!trimmed.Contains(":"))
                {
                    if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rawSec))
                        return (int)Math.Round(rawSec);
                    return 0;
                }

                string[] parts = trimmed.Split(':');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0].Trim(), out int m2) &&
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s2))
                {
                    return m2 * 60 + (int)Math.Round(s2);
                }
                if (parts.Length == 3 &&
                    int.TryParse(parts[0].Trim(), out int h3) &&
                    int.TryParse(parts[1].Trim(), out int m3) &&
                    double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s3))
                {
                    return h3 * 3600 + m3 * 60 + (int)Math.Round(s3);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("PARSER", "Falha em ParseDurationToSeconds ('" + dur + "'): " + ex.Message);
            }
            return 0;
        }

        public static string FormatSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "0:00";
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return string.Format("{0}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format("{0}:{1:D2}", ts.Minutes, ts.Seconds);
        }
    }
}

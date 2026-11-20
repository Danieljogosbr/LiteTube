#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    public sealed class AccountFeedPage
    {
        public List<TrackItem> Items { get; set; } = new List<TrackItem>();
        public string ContinuationToken { get; set; } = string.Empty;
        public bool UsedAccountAuth { get; set; }
        public bool SessionExpired { get; set; }
        public bool FromCache { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Camada 3 — Feeds & Biblioteca da Conta Google via InnerTube Web Direto (somente leitura).
    /// Substitui chamadas lentas de yt-dlp para navegar Início, Inscrições, Histórico e Playlists,
    /// com cache limitado em RAM (TTL 3 min), paginação por continuationToken e suporte a JSON clássico e lockupViewModel.
    /// </summary>
    public sealed class YouTubeAccountFeedService
    {
        private const string BrowseEndpoint = "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";
        private const string MusicBrowseEndpoint = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false";
        private const int MaxCachedPages = 12;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

        public static YouTubeAccountFeedService Shared { get; } = new YouTubeAccountFeedService(GoogleSessionService.Shared);

        private readonly GoogleSessionService _session;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, (AccountFeedPage Page, DateTime ExpiresUtc)> _pageCache =
            new Dictionary<string, (AccountFeedPage, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _cacheOrder = new Queue<string>();

        private static readonly HashSet<string> ExcludedSpecialPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WL", "LL", "LM", "HL", "SE"
        };

        private static readonly Regex _rxVideoIdFromUrl = new Regex(
            @"/vi(?:_webp)?/([a-zA-Z0-9_-]{11})/",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public YouTubeAccountFeedService(GoogleSessionService session)
        {
            _session = session ?? GoogleSessionService.Shared;
        }

        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _pageCache.Clear();
                _cacheOrder.Clear();
            }
        }

        public Task<AccountFeedPage> FetchHomeFeedAsync(
            AppConfig config,
            bool forceRefresh = false,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            return FetchBrowseVideoFeedAsync("home", "FEwhat_to_watch", config, forceRefresh, continuationToken, ct);
        }

        public Task<AccountFeedPage> FetchSubscriptionsFeedAsync(
            AppConfig config,
            bool forceRefresh = false,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            return FetchBrowseVideoFeedAsync("subs", "FEsubscriptions", config, forceRefresh, continuationToken, ct);
        }

        public async Task<AccountFeedPage> FetchHistoryFeedAsync(
            AppConfig config,
            bool forceRefresh = false,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            // Paginação de páginas seguintes do histórico Google
            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                return await FetchBrowseVideoFeedAsync("history", "FEhistory", config, true, continuationToken, ct).ConfigureAwait(false);
            }

            var localItems = WatchHistoryService.Instance.GetHistoryAsTrackItems();
            var remotePage = await FetchBrowseVideoFeedAsync("history", "FEhistory", config, forceRefresh, null, ct).ConfigureAwait(false);

            var merged = new List<TrackItem>(localItems.Count + remotePage.Items.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Histórico local do LiteTube primeiro (mais recente nesta máquina, com retomada exata)
            for (int i = 0; i < localItems.Count; i++)
            {
                var item = localItems[i];
                if (item != null && !string.IsNullOrEmpty(item.Id) && seen.Add(item.Id))
                {
                    merged.Add(item);
                }
            }

            // 2. Histórico da conta Google logo em seguida, sem duplicar
            for (int i = 0; i < remotePage.Items.Count; i++)
            {
                var item = remotePage.Items[i];
                if (item != null && !string.IsNullOrEmpty(item.Id) && seen.Add(item.Id))
                {
                    merged.Add(item);
                }
            }

            return new AccountFeedPage
            {
                Items = merged,
                ContinuationToken = remotePage.ContinuationToken,
                UsedAccountAuth = remotePage.UsedAccountAuth,
                SessionExpired = remotePage.SessionExpired,
                FromCache = remotePage.FromCache,
                StatusMessage = remotePage.StatusMessage
            };
        }

        public async Task<AccountFeedPage> FetchAccountPlaylistsAsync(
            AppConfig config,
            bool forceRefresh = false,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            bool isFirstPage = string.IsNullOrWhiteSpace(continuationToken);
            if (isFirstPage && !forceRefresh && TryGetCachedPage("playlists", out var cached))
            {
                return ClonePageAsCached(cached);
            }

            var sw = Stopwatch.StartNew();
            var headers = _session.BuildWebHeaders(config, out bool hasAuthCookies);
            if (!hasAuthCookies)
            {
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = "Conecte sua Conta Google para visualizar suas playlists do YouTube."
                };
            }

            try
            {
                string payload = isFirstPage
                    ? BuildBrowsePayload("FEplaylist_aggregation", null)
                    : BuildBrowsePayload(null, continuationToken);

                string json = await HttpPipeline.PostJsonAsync(BrowseEndpoint, payload, "https://www.youtube.com/feed/playlists", headers, ct).ConfigureAwait(false);
                var page = ParsePlaylistsJson(json, hasAuthCookies);

                // Fallback: algumas contas retornam a coleção completa sob FElibrary
                if (isFirstPage && page.Items.Count == 0 && !page.SessionExpired)
                {
                    string libPayload = BuildBrowsePayload("FElibrary", null);
                    string libJson = await HttpPipeline.PostJsonAsync(BrowseEndpoint, libPayload, "https://www.youtube.com/feed/you", headers, ct).ConfigureAwait(false);
                    var libPage = ParsePlaylistsJson(libJson, hasAuthCookies);
                    if (libPage.Items.Count > 0 || libPage.SessionExpired)
                    {
                        page = libPage;
                    }
                }

                if (page.SessionExpired)
                {
                    _session.MarkSessionExpired();
                    page.StatusMessage = "Sua sessão Google expirou. Clique em 'Conta Google' para reconectar.";
                }
                else if (isFirstPage && page.Items.Count > 0)
                {
                    StoreCachedPage("playlists", page);
                }

                Logger.Info("FEED", string.Format("Playlists da conta carregadas via InnerTube em {0}ms: {1} playlist(s) (Auth={2}, Expired={3})",
                    sw.ElapsedMilliseconds, page.Items.Count, page.UsedAccountAuth, page.SessionExpired));

                return page;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao carregar playlists da conta via InnerTube: " + ex.Message);
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = "Não foi possível carregar suas playlists agora. Verifique a conexão."
                };
            }
        }

        public async Task<AccountFeedPage> FetchPlaylistItemsAsync(
            string playlistId,
            AppConfig config,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playlistId))
                return new AccountFeedPage();

            string cleanId = playlistId.Trim();
            if (cleanId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
                cleanId = cleanId.Substring(2);

            var sw = Stopwatch.StartNew();
            var headers = _session.BuildWebHeaders(config, out bool hasAuthCookies);

            try
            {
                string payload = string.IsNullOrWhiteSpace(continuationToken)
                    ? BuildBrowsePayload("VL" + cleanId, null)
                    : BuildBrowsePayload(null, continuationToken);

                string json = await HttpPipeline.PostJsonAsync(
                    BrowseEndpoint,
                    payload,
                    "https://www.youtube.com/playlist?list=" + cleanId,
                    headers,
                    ct).ConfigureAwait(false);

                var page = ParseWebPlaylistItemsJson(json, hasAuthCookies);
                Logger.Info("FEED", string.Format("Itens da playlist '{0}' carregados via InnerTube em {1}ms: {2} vídeo(s)",
                    cleanId, sw.ElapsedMilliseconds, page.Items.Count));
                return page;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", string.Format("Falha InnerTube ao abrir playlist '{0}': {1}", cleanId, ex.Message));
                return new AccountFeedPage();
            }
        }

        private async Task<AccountFeedPage> FetchBrowseVideoFeedAsync(
            string cacheKey,
            string browseId,
            AppConfig config,
            bool forceRefresh,
            string? continuationToken,
            CancellationToken ct)
        {
            bool isFirstPage = string.IsNullOrWhiteSpace(continuationToken);
            if (isFirstPage && !forceRefresh && TryGetCachedPage(cacheKey, out var cached))
            {
                return ClonePageAsCached(cached);
            }

            var sw = Stopwatch.StartNew();
            var headers = _session.BuildWebHeaders(config, out bool hasAuthCookies);

            if (!hasAuthCookies && (browseId == "FEsubscriptions" || browseId == "FEhistory"))
            {
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = browseId == "FEsubscriptions"
                        ? "Conecte sua Conta Google para ver as inscrições dos seus canais."
                        : "Histórico local ativo (conecte sua Conta Google para sincronizar o histórico da conta)."
                };
            }

            try
            {
                string payload = isFirstPage
                    ? BuildBrowsePayload(browseId, null)
                    : BuildBrowsePayload(null, continuationToken);

                string referer = browseId switch
                {
                    "FEsubscriptions" => "https://www.youtube.com/feed/subscriptions",
                    "FEhistory" => "https://www.youtube.com/feed/history",
                    _ => "https://www.youtube.com/"
                };

                string json = await HttpPipeline.PostJsonAsync(BrowseEndpoint, payload, referer, headers, ct).ConfigureAwait(false);
                var page = ParseVideoFeedJson(json, hasAuthCookies);

                if (page.SessionExpired)
                {
                    _session.MarkSessionExpired();
                    page.StatusMessage = "Sua sessão Google expirou. Clique em 'Conta Google' para reconectar.";
                }
                else if (isFirstPage && page.Items.Count > 0)
                {
                    StoreCachedPage(cacheKey, page);
                }

                Logger.Info("FEED", string.Format("Feed '{0}' ({1}) carregado via InnerTube em {2}ms: {3} itens (Auth={4}, Expired={5}, NextPage={6})",
                    cacheKey, browseId, sw.ElapsedMilliseconds, page.Items.Count,
                    page.UsedAccountAuth, page.SessionExpired, !string.IsNullOrEmpty(page.ContinuationToken)));

                return page;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", string.Format("Falha InnerTube ao carregar feed '{0}': {1}", browseId, ex.Message));
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = "Não foi possível carregar este feed agora. Tente atualizar."
                };
            }
        }

        public async Task<AccountFeedPage> FetchMusicAccountPlaylistsAsync(
            AppConfig config,
            bool forceRefresh = false,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            bool isFirstPage = string.IsNullOrWhiteSpace(continuationToken);
            if (isFirstPage && !forceRefresh && TryGetCachedPage("music_playlists", out var cached))
            {
                return ClonePageAsCached(cached);
            }

            var sw = Stopwatch.StartNew();
            var headers = _session.BuildMusicHeaders(config, out bool hasAuthCookies);
            if (!hasAuthCookies)
            {
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = "Conecte sua Conta Google para visualizar suas playlists do YouTube Music."
                };
            }

            try
            {
                string payload = isFirstPage
                    ? BuildMusicBrowsePayload("FEmusic_liked_playlists", null)
                    : BuildMusicBrowsePayload(null, continuationToken);

                string json = await HttpPipeline.PostJsonAsync(
                    MusicBrowseEndpoint,
                    payload,
                    "https://music.youtube.com/library/playlists",
                    headers,
                    ct).ConfigureAwait(false);

                var page = ParseMusicPlaylistsJson(json, hasAuthCookies);

                // Fallback: tenta FEmusic_library_landing caso FEmusic_liked_playlists venha vazio
                if (isFirstPage && page.Items.Count == 0 && !page.SessionExpired)
                {
                    string libPayload = BuildMusicBrowsePayload("FEmusic_library_landing", null);
                    string libJson = await HttpPipeline.PostJsonAsync(
                        MusicBrowseEndpoint,
                        libPayload,
                        "https://music.youtube.com/library",
                        headers,
                        ct).ConfigureAwait(false);
                    var libPage = ParseMusicPlaylistsJson(libJson, hasAuthCookies);
                    if (libPage.Items.Count > 0 || libPage.SessionExpired)
                    {
                        page = libPage;
                    }
                }

                if (page.SessionExpired)
                {
                    _session.MarkSessionExpired();
                    page.StatusMessage = "Sua sessão Google expirou. Clique em 'Conta Google' para reconectar.";
                }
                else if (isFirstPage && page.Items.Count > 0)
                {
                    StoreCachedPage("music_playlists", page);
                }

                Logger.Info("FEED", string.Format("Playlists YT Music carregadas via InnerTube em {0}ms: {1} playlist(s) (Auth={2}, Expired={3})",
                    sw.ElapsedMilliseconds, page.Items.Count, page.UsedAccountAuth, page.SessionExpired));

                return page;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao carregar playlists do YouTube Music: " + ex.Message);
                return new AccountFeedPage
                {
                    UsedAccountAuth = false,
                    StatusMessage = "Não foi possível carregar suas playlists do YouTube Music agora."
                };
            }
        }

        public async Task<AccountFeedPage> FetchMusicPlaylistItemsAsync(
            string playlistId,
            AppConfig config,
            string? continuationToken = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playlistId))
                return new AccountFeedPage();

            string cleanId = playlistId.Trim();
            if (cleanId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
                cleanId = cleanId.Substring(2);

            var sw = Stopwatch.StartNew();
            var headers = _session.BuildMusicHeaders(config, out bool hasAuthCookies);

            try
            {
                string payload = string.IsNullOrWhiteSpace(continuationToken)
                    ? BuildMusicBrowsePayload("VL" + cleanId, null)
                    : BuildMusicBrowsePayload(null, continuationToken);

                string json = await HttpPipeline.PostJsonAsync(
                    MusicBrowseEndpoint,
                    payload,
                    "https://music.youtube.com/playlist?list=" + cleanId,
                    headers,
                    ct).ConfigureAwait(false);

                var page = ParseMusicPlaylistItemsJson(json, hasAuthCookies);
                Logger.Info("FEED", string.Format("Faixas da playlist YT Music '{0}' carregadas em {1}ms: {2} faixa(s)",
                    cleanId, sw.ElapsedMilliseconds, page.Items.Count));

                if (page.Items.Count > 0 || !string.IsNullOrWhiteSpace(continuationToken))
                    return page;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", string.Format("Falha WEB_REMIX ao abrir playlist '{0}': {1}", cleanId, ex.Message));
            }

            // Fallback transparente para o endpoint www.youtube.com VL<id>
            return await FetchPlaylistItemsAsync(cleanId, config, continuationToken, ct).ConfigureAwait(false);
        }

        private static string BuildMusicBrowsePayload(string? browseId, string? continuationToken)
        {
            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                string escCont = JsonEncodedText.Encode(continuationToken).ToString();
                return "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"" +
                       GoogleSessionService.MusicClientVersion +
                       "\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"continuation\":\"" + escCont + "\"}";
            }

            string escBrowse = JsonEncodedText.Encode(browseId ?? "FEmusic_liked_playlists").ToString();
            return "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"" +
                   GoogleSessionService.MusicClientVersion +
                   "\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" + escBrowse + "\"}";
        }

        private static string BuildBrowsePayload(string? browseId, string? continuationToken)
        {
            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                string escCont = JsonEncodedText.Encode(continuationToken).ToString();
                return "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" +
                       GoogleSessionService.WebClientVersion +
                       "\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"continuation\":\"" + escCont + "\"}";
            }

            string escBrowse = JsonEncodedText.Encode(browseId ?? "FEwhat_to_watch").ToString();
            return "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" +
                   GoogleSessionService.WebClientVersion +
                   "\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"" + escBrowse + "\"}";
        }

        private bool TryGetCachedPage(string key, out AccountFeedPage page)
        {
            lock (_cacheLock)
            {
                if (_pageCache.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow < entry.ExpiresUtc)
                    {
                        page = entry.Page;
                        return true;
                    }
                    _pageCache.Remove(key);
                }
            }
            page = new AccountFeedPage();
            return false;
        }

        private void StoreCachedPage(string key, AccountFeedPage page)
        {
            lock (_cacheLock)
            {
                if (!_pageCache.ContainsKey(key))
                {
                    _cacheOrder.Enqueue(key);
                }
                _pageCache[key] = (page, DateTime.UtcNow.Add(CacheTtl));

                while (_pageCache.Count > MaxCachedPages && _cacheOrder.Count > 0)
                {
                    string oldest = _cacheOrder.Dequeue();
                    _pageCache.Remove(oldest);
                }
            }
        }

        private static AccountFeedPage ClonePageAsCached(AccountFeedPage source)
        {
            return new AccountFeedPage
            {
                Items = new List<TrackItem>(source.Items),
                ContinuationToken = source.ContinuationToken,
                UsedAccountAuth = source.UsedAccountAuth,
                SessionExpired = source.SessionExpired,
                StatusMessage = source.StatusMessage,
                FromCache = true
            };
        }

        // =========================================================================
        // PARSERS NATIVOS SYSTEM.TEXT.JSON (Suporte a renderers clássicos e 2025/2026)
        // =========================================================================

        internal static AccountFeedPage ParseVideoFeedJson(string? json, bool sentAuthCookies)
        {
            var page = new AccountFeedPage();
            if (string.IsNullOrWhiteSpace(json)) return page;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string continuation = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool? loggedIn = GoogleSessionService.DetectLoggedInState(root);
                if (loggedIn.HasValue)
                {
                    page.UsedAccountAuth = loggedIn.Value;
                    page.SessionExpired = sentAuthCookies && !loggedIn.Value;
                }
                else
                {
                    page.UsedAccountAuth = sentAuthCookies;
                }

                WalkForVideos(root, page.Items, seenIds, ref continuation);
                page.ContinuationToken = continuation;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao interpretar JSON de feed de vídeos: " + ex.Message);
            }

            return page;
        }

        internal static AccountFeedPage ParsePlaylistsJson(string? json, bool sentAuthCookies)
        {
            var page = new AccountFeedPage();
            if (string.IsNullOrWhiteSpace(json)) return page;

            var seenPl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string continuation = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool? loggedIn = GoogleSessionService.DetectLoggedInState(root);
                if (loggedIn.HasValue)
                {
                    page.UsedAccountAuth = loggedIn.Value;
                    page.SessionExpired = sentAuthCookies && !loggedIn.Value;
                }
                else
                {
                    page.UsedAccountAuth = sentAuthCookies;
                }

                WalkForPlaylists(root, page.Items, seenPl, ref continuation);
                page.ContinuationToken = continuation;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao interpretar JSON de playlists da conta: " + ex.Message);
            }

            return page;
        }

        private static void WalkForVideos(JsonElement el, List<TrackItem> items, HashSet<string> seenIds, ref string continuation)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name;
                    var val = prop.Value;

                    // Ignora prateleiras exclusivas de Shorts para manter o feed limpo
                    if (name == "reelShelfRenderer" || name == "shortsLockupViewModel" || name == "reelItemRenderer")
                    {
                        continue;
                    }

                    if (name == "videoRenderer" || name == "gridVideoRenderer" || name == "playlistVideoRenderer")
                    {
                        var item = ParseClassicVideoRenderer(val);
                        if (item != null && seenIds.Add(item.Id))
                        {
                            items.Add(item);
                        }
                        continue;
                    }

                    if (name == "lockupViewModel")
                    {
                        var item = ParseVideoLockupViewModel(val);
                        if (item != null && seenIds.Add(item.Id))
                        {
                            items.Add(item);
                        }
                        continue;
                    }

                    if (name == "continuationItemRenderer" && string.IsNullOrEmpty(continuation))
                    {
                        string tok = ExtractContinuationFromItem(val);
                        if (!string.IsNullOrEmpty(tok))
                        {
                            continuation = tok;
                        }
                        continue;
                    }

                    WalkForVideos(val, items, seenIds, ref continuation);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    WalkForVideos(child, items, seenIds, ref continuation);
                }
            }
        }

        private static void WalkForPlaylists(JsonElement el, List<TrackItem> items, HashSet<string> seenPl, ref string continuation)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name;
                    var val = prop.Value;

                    if (name == "playlistRenderer" || name == "gridPlaylistRenderer")
                    {
                        var pl = ParseClassicPlaylistRenderer(val);
                        if (pl != null && !IsExcludedPlaylist(pl.PlaylistId, pl.Title) && seenPl.Add(pl.PlaylistId))
                        {
                            items.Add(pl);
                        }
                        continue;
                    }

                    if (name == "lockupViewModel")
                    {
                        var pl = ParsePlaylistLockupViewModel(val);
                        if (pl != null && !IsExcludedPlaylist(pl.PlaylistId, pl.Title) && seenPl.Add(pl.PlaylistId))
                        {
                            items.Add(pl);
                        }
                        continue;
                    }

                    if (name == "continuationItemRenderer" && string.IsNullOrEmpty(continuation))
                    {
                        string tok = ExtractContinuationFromItem(val);
                        if (!string.IsNullOrEmpty(tok))
                        {
                            continuation = tok;
                        }
                        continue;
                    }

                    WalkForPlaylists(val, items, seenPl, ref continuation);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    WalkForPlaylists(child, items, seenPl, ref continuation);
                }
            }
        }

        private static TrackItem? ParseClassicVideoRenderer(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string vidId = ReadString(obj, "videoId");
            if (string.IsNullOrEmpty(vidId) || vidId.Length != 11) return null;

            string title = ReadText(obj, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            if (title == "[Private video]" || title == "[Deleted video]" ||
                title == "[Vídeo privado]" || title == "[Vídeo excluído]")
                return null;

            string artist = ReadText(obj, "ownerText");
            if (string.IsNullOrEmpty(artist)) artist = ReadText(obj, "longBylineText");
            if (string.IsNullOrEmpty(artist)) artist = ReadText(obj, "shortBylineText");
            if (string.IsNullOrEmpty(artist)) artist = "YouTube";

            string channelId = ExtractBrowseIdFromOwner(obj, "ownerText");
            if (string.IsNullOrEmpty(channelId)) channelId = ExtractBrowseIdFromOwner(obj, "longBylineText");
            if (string.IsNullOrEmpty(channelId)) channelId = ExtractBrowseIdFromOwner(obj, "shortBylineText");

            string durText = ReadText(obj, "lengthText");
            int durSec = 0;
            string lenSecRaw = ReadString(obj, "lengthSeconds");
            if (!string.IsNullOrEmpty(lenSecRaw) && int.TryParse(lenSecRaw, out int parsedSec))
            {
                durSec = parsedSec;
            }
            else
            {
                durSec = YouTubeService.ParseDurationToSeconds(durText);
            }

            if (string.IsNullOrEmpty(durText) && durSec > 0)
            {
                durText = YouTubeService.FormatSeconds(durSec);
            }

            bool isLive = IsLiveBadgePresent(obj) ||
                          (string.IsNullOrEmpty(durText) && durSec <= 0);
            if (isLive && string.IsNullOrEmpty(durText))
            {
                durText = "AO VIVO";
            }

            string viewsText = ReadText(obj, "shortViewCountText");
            if (string.IsNullOrEmpty(viewsText)) viewsText = ReadText(obj, "viewCountText");

            string publishedText = ReadText(obj, "publishedTimeText");

            // Em playlistVideoRenderer, visualizações e data costumam vir em videoInfo.runs
            if (string.IsNullOrEmpty(viewsText) && obj.TryGetProperty("videoInfo", out var viEl))
            {
                ExtractViewsAndDateFromRuns(viEl, ref viewsText, ref publishedText);
            }

            string avatarUrl = ExtractClassicChannelAvatar(obj);
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                YouTubeService.CacheChannelAvatarUrl(channelId, artist, avatarUrl);
            }

            string thumbUrl = "https://i.ytimg.com/vi/" + vidId + "/hqdefault.jpg";

            bool isShort = (!isLive && durSec > 0 && durSec <= 60 &&
                            (title.IndexOf("#shorts", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             HasShortsNavigationEndpoint(obj)));

            return new TrackItem
            {
                Id = vidId,
                Title = title,
                Artist = artist,
                ChannelId = channelId,
                ChannelAvatarUrl = avatarUrl,
                Duration = string.IsNullOrEmpty(durText) ? "--:--" : durText,
                DurationSeconds = durSec,
                ViewCountText = viewsText,
                PublishedTimeText = publishedText,
                CustomThumbUrl = thumbUrl,
                ItemType = isShort ? "short" : "video"
            };
        }

        private static TrackItem? ParseVideoLockupViewModel(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string contentType = ReadString(obj, "contentType");
            if (!string.IsNullOrEmpty(contentType) &&
                !string.Equals(contentType, "LOCKUP_CONTENT_TYPE_VIDEO", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string vidId = ReadString(obj, "contentId");
            if (string.IsNullOrEmpty(vidId) || vidId.Length != 11) return null;

            string title = "";
            string artist = "";
            string channelId = "";
            string avatarUrl = "";
            string viewsText = "";
            string publishedText = "";

            if (obj.TryGetProperty("metadata", out var metaWrap) &&
                metaWrap.TryGetProperty("lockupMetadataViewModel", out var metaVm))
            {
                title = ReadText(metaVm, "title");

                if (metaVm.TryGetProperty("image", out var imgWrap) &&
                    imgWrap.TryGetProperty("decoratedAvatarViewModel", out var decAv))
                {
                    avatarUrl = ExtractNestedImageUrl(decAv);
                    channelId = ExtractFirstChannelBrowseId(decAv);
                }

                if (metaVm.TryGetProperty("metadata", out var innerMeta) &&
                    innerMeta.TryGetProperty("contentMetadataViewModel", out var cmVm) &&
                    cmVm.TryGetProperty("metadataRows", out var rows) &&
                    rows.ValueKind == JsonValueKind.Array)
                {
                    int rowIdx = 0;
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (!row.TryGetProperty("metadataParts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var part in parts.EnumerateArray())
                        {
                            string txt = ReadText(part, "text");
                            if (string.IsNullOrWhiteSpace(txt)) continue;

                            if (rowIdx == 0 && string.IsNullOrEmpty(artist))
                            {
                                artist = txt;
                                if (string.IsNullOrEmpty(channelId))
                                {
                                    channelId = ExtractFirstChannelBrowseId(part);
                                }
                            }
                            else if (LooksLikeViews(txt) && string.IsNullOrEmpty(viewsText))
                            {
                                viewsText = txt;
                            }
                            else if (LooksLikeDate(txt) && string.IsNullOrEmpty(publishedText))
                            {
                                publishedText = txt;
                            }
                            else if (string.IsNullOrEmpty(artist))
                            {
                                artist = txt;
                            }
                        }
                        rowIdx++;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title)) return null;
            if (string.IsNullOrEmpty(artist)) artist = "YouTube";

            string durText = ExtractLockupBadgeText(obj);
            int durSec = YouTubeService.ParseDurationToSeconds(durText);
            bool isLive = durText.IndexOf("AO VIVO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          durText.IndexOf("LIVE", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!string.IsNullOrEmpty(avatarUrl))
            {
                avatarUrl = YouTubeService.NormalizeAvatarUrl(avatarUrl);
                if (!string.IsNullOrEmpty(avatarUrl))
                {
                    YouTubeService.CacheChannelAvatarUrl(channelId, artist, avatarUrl);
                }
            }

            bool isShort = (!isLive && durSec > 0 && durSec <= 60 && title.IndexOf("#shorts", StringComparison.OrdinalIgnoreCase) >= 0);

            return new TrackItem
            {
                Id = vidId,
                Title = title,
                Artist = artist,
                ChannelId = channelId,
                ChannelAvatarUrl = avatarUrl,
                Duration = string.IsNullOrEmpty(durText) ? (isLive ? "AO VIVO" : "--:--") : durText,
                DurationSeconds = durSec,
                ViewCountText = viewsText,
                PublishedTimeText = publishedText,
                CustomThumbUrl = "https://i.ytimg.com/vi/" + vidId + "/hqdefault.jpg",
                ItemType = isShort ? "short" : "video"
            };
        }

        private static TrackItem? ParseClassicPlaylistRenderer(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string plId = ReadString(obj, "playlistId");
            if (string.IsNullOrWhiteSpace(plId)) return null;

            string title = ReadText(obj, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;

            string countText = ReadText(obj, "videoCountShortText");
            if (string.IsNullOrEmpty(countText)) countText = ReadText(obj, "videoCountText");
            if (string.IsNullOrEmpty(countText)) countText = ReadString(obj, "videoCount");

            string owner = ReadText(obj, "shortBylineText");
            if (string.IsNullOrEmpty(owner)) owner = ReadText(obj, "longBylineText");

            string thumbUrl = ExtractBestThumbnailUrl(obj);
            string coverVidId = ExtractVideoIdFromThumbUrl(thumbUrl);
            if (!string.IsNullOrEmpty(coverVidId))
            {
                thumbUrl = "https://i.ytimg.com/vi/" + coverVidId + "/hqdefault.jpg";
            }

            string subtitle = !string.IsNullOrEmpty(owner) ? owner : "Playlist";
            if (!string.IsNullOrEmpty(countText))
            {
                string countFormatted = Regex.IsMatch(countText, @"^\d+$") ? (countText + " vídeos") : countText;
                subtitle += " • " + countFormatted;
            }

            return new TrackItem
            {
                Id = !string.IsNullOrEmpty(coverVidId) ? coverVidId : ("PL_" + plId),
                PlaylistId = plId,
                Title = title,
                Artist = subtitle,
                Duration = "PLAYLIST",
                ItemType = "playlist",
                CustomThumbUrl = thumbUrl
            };
        }

        private static TrackItem? ParsePlaylistLockupViewModel(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string contentType = ReadString(obj, "contentType");
            string contentId = ReadString(obj, "contentId");

            bool isPlaylistType = string.Equals(contentType, "LOCKUP_CONTENT_TYPE_PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(contentType, "LOCKUP_CONTENT_TYPE_PODCAST", StringComparison.OrdinalIgnoreCase);

            string plId = isPlaylistType ? contentId : "";
            if (string.IsNullOrEmpty(plId) &&
                (contentId.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                 contentId.StartsWith("OL", StringComparison.OrdinalIgnoreCase) ||
                 contentId.StartsWith("UU", StringComparison.OrdinalIgnoreCase)))
            {
                plId = contentId;
            }

            if (string.IsNullOrEmpty(plId))
            {
                plId = ExtractPlaylistIdFromContext(obj);
            }

            if (string.IsNullOrWhiteSpace(plId)) return null;

            string title = "";
            string subtitleParts = "";
            if (obj.TryGetProperty("metadata", out var metaWrap) &&
                metaWrap.TryGetProperty("lockupMetadataViewModel", out var metaVm))
            {
                title = ReadText(metaVm, "title");
                if (metaVm.TryGetProperty("metadata", out var innerMeta) &&
                    innerMeta.TryGetProperty("contentMetadataViewModel", out var cmVm) &&
                    cmVm.TryGetProperty("metadataRows", out var rows) &&
                    rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (!row.TryGetProperty("metadataParts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var part in parts.EnumerateArray())
                        {
                            string t = ReadText(part, "text");
                            if (string.IsNullOrWhiteSpace(t)) continue;
                            if (t.IndexOf("Ver playlist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                t.IndexOf("View full playlist", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                            if (string.IsNullOrEmpty(subtitleParts))
                                subtitleParts = t;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title)) return null;

            string badgeCount = ExtractLockupBadgeText(obj);
            string thumbUrl = ExtractNestedImageUrl(obj);
            string coverVidId = ExtractVideoIdFromThumbUrl(thumbUrl);
            if (!string.IsNullOrEmpty(coverVidId))
            {
                thumbUrl = "https://i.ytimg.com/vi/" + coverVidId + "/hqdefault.jpg";
            }

            string artistLabel = !string.IsNullOrEmpty(subtitleParts) ? subtitleParts : "Playlist";
            if (!string.IsNullOrEmpty(badgeCount) && artistLabel.IndexOf(badgeCount, StringComparison.OrdinalIgnoreCase) < 0)
            {
                artistLabel += " • " + badgeCount;
            }

            return new TrackItem
            {
                Id = !string.IsNullOrEmpty(coverVidId) ? coverVidId : ("PL_" + plId),
                PlaylistId = plId,
                Title = title,
                Artist = artistLabel,
                Duration = "PLAYLIST",
                ItemType = "playlist",
                CustomThumbUrl = thumbUrl
            };
        }

        internal static bool IsExcludedPlaylist(string? playlistId, string? title)
        {
            if (string.IsNullOrWhiteSpace(playlistId)) return true;

            string id = playlistId.Trim();
            if (id.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && id.Length > 2)
                id = id.Substring(2);

            if (ExcludedSpecialPlaylistIds.Contains(id))
                return true;

            // Exclui Mixes automáticos do YouTube (RD...)
            if (id.StartsWith("RD", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(title))
            {
                string t = title.Trim();
                if (string.Equals(t, "Assistir mais tarde", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "Watch later", StringComparison.OrdinalIgnoreCase) ||
                    t.IndexOf("Vídeos marcados com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("Músicas marcadas com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("Episódios para mais tarde", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(t, "Liked videos", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "Liked Music", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "Episodes for Later", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "Nova playlist", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "New playlist", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // =========================================================================
        // PARSERS NATIVOS YOUTUBE MUSIC (WEB_REMIX: Playlists da Conta & Faixas)
        // =========================================================================

        internal static AccountFeedPage ParseMusicPlaylistsJson(string? json, bool sentAuthCookies)
        {
            var page = new AccountFeedPage();
            if (string.IsNullOrWhiteSpace(json)) return page;

            var seenPl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string continuation = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool? loggedIn = GoogleSessionService.DetectLoggedInState(root);
                if (loggedIn.HasValue)
                {
                    page.UsedAccountAuth = loggedIn.Value;
                    page.SessionExpired = sentAuthCookies && !loggedIn.Value;
                }
                else
                {
                    page.UsedAccountAuth = sentAuthCookies;
                }

                WalkForMusicPlaylists(root, page.Items, seenPl, ref continuation);
                page.ContinuationToken = continuation;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao interpretar JSON de playlists do YT Music: " + ex.Message);
            }

            return page;
        }

        internal static AccountFeedPage ParseWebPlaylistItemsJson(string? json, bool sentAuthCookies)
        {
            var page = new AccountFeedPage();
            if (string.IsNullOrWhiteSpace(json)) return page;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string continuation = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool? loggedIn = GoogleSessionService.DetectLoggedInState(root);
                if (loggedIn.HasValue)
                {
                    page.UsedAccountAuth = loggedIn.Value;
                    page.SessionExpired = sentAuthCookies && !loggedIn.Value;
                }
                else
                {
                    page.UsedAccountAuth = sentAuthCookies;
                }

                // Escopo estrito ao bloco da playlist (evita misturar prateleiras de recomendados abaixo da playlist)
                if (TryFindPropertyRecursive(root, "playlistVideoListRenderer", out var plListEl))
                {
                    WalkForVideos(plListEl, page.Items, seenIds, ref continuation);
                }
                else if (TryFindPropertyRecursive(root, "appendContinuationItemsAction", out var contEl))
                {
                    WalkForVideos(contEl, page.Items, seenIds, ref continuation);
                }

                page.ContinuationToken = continuation;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao interpretar JSON de itens de playlist Web: " + ex.Message);
            }

            return page;
        }

        internal static AccountFeedPage ParseMusicPlaylistItemsJson(string? json, bool sentAuthCookies)
        {
            var page = new AccountFeedPage();
            if (string.IsNullOrWhiteSpace(json)) return page;

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string continuation = string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool? loggedIn = GoogleSessionService.DetectLoggedInState(root);
                if (loggedIn.HasValue)
                {
                    page.UsedAccountAuth = loggedIn.Value;
                    page.SessionExpired = sentAuthCookies && !loggedIn.Value;
                }
                else
                {
                    page.UsedAccountAuth = sentAuthCookies;
                }

                // Escopo estrito à prateleira da própria playlist (ignora "Sugestões" em musicShelfRenderer
                // e ignora tokens de continuação do sectionListRenderer externo que carregam recomendações!)
                if (TryFindPropertyRecursive(root, "musicPlaylistShelfRenderer", out var plShelf))
                {
                    WalkForMusicPlaylistTracks(plShelf, page.Items, seenIds, ref continuation);
                }
                else if (TryFindPropertyRecursive(root, "musicPlaylistShelfContinuation", out var plCont))
                {
                    WalkForMusicPlaylistTracks(plCont, page.Items, seenIds, ref continuation);
                }
                else if (TryFindPropertyRecursive(root, "appendContinuationItemsAction", out var appendCont))
                {
                    WalkForMusicPlaylistTracks(appendCont, page.Items, seenIds, ref continuation);
                }

                page.ContinuationToken = continuation;
            }
            catch (Exception ex)
            {
                Logger.Warn("FEED", "Erro ao interpretar JSON de faixas de playlist do YT Music: " + ex.Message);
            }

            return page;
        }

        private static bool TryFindPropertyRecursive(JsonElement element, string propertyName, out JsonElement found)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, propertyName, StringComparison.Ordinal))
                    {
                        found = prop.Value;
                        return true;
                    }
                    if (TryFindPropertyRecursive(prop.Value, propertyName, out found))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    if (TryFindPropertyRecursive(child, propertyName, out found))
                        return true;
                }
            }

            found = default;
            return false;
        }

        private static void WalkForMusicPlaylists(JsonElement el, List<TrackItem> items, HashSet<string> seenPl, ref string continuation)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name;
                    var val = prop.Value;

                    if (name == "musicTwoRowItemRenderer")
                    {
                        var pl = ParseMusicTwoRowPlaylist(val);
                        if (pl != null && !IsExcludedPlaylist(pl.PlaylistId, pl.Title) && seenPl.Add(pl.PlaylistId))
                        {
                            items.Add(pl);
                        }
                        continue;
                    }

                    if (name == "playlistRenderer" || name == "gridPlaylistRenderer")
                    {
                        var pl = ParseClassicPlaylistRenderer(val);
                        if (pl != null && !IsExcludedPlaylist(pl.PlaylistId, pl.Title) && seenPl.Add(pl.PlaylistId))
                        {
                            items.Add(pl);
                        }
                        continue;
                    }

                    if ((name == "continuationItemRenderer" || name == "nextContinuationData") && string.IsNullOrEmpty(continuation))
                    {
                        string tok = name == "nextContinuationData"
                            ? ReadString(val, "continuation")
                            : ExtractContinuationFromItem(val);
                        if (!string.IsNullOrEmpty(tok))
                            continuation = tok;
                        continue;
                    }

                    WalkForMusicPlaylists(val, items, seenPl, ref continuation);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    WalkForMusicPlaylists(child, items, seenPl, ref continuation);
                }
            }
        }

        private static TrackItem? ParseMusicTwoRowPlaylist(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string title = ReadText(obj, "title");
            if (string.IsNullOrWhiteSpace(title)) return null;

            string plId = "";
            if (obj.TryGetProperty("navigationEndpoint", out var nav))
            {
                if (nav.TryGetProperty("browseEndpoint", out var be))
                {
                    string bid = ReadString(be, "browseId");
                    if (bid.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && bid.Length > 2)
                        plId = bid.Substring(2);
                    else if (bid.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                             bid.StartsWith("OL", StringComparison.OrdinalIgnoreCase))
                        plId = bid;
                }
                if (string.IsNullOrEmpty(plId) && nav.TryGetProperty("watchPlaylistEndpoint", out var wpe))
                {
                    plId = ReadString(wpe, "playlistId");
                }
            }

            if (string.IsNullOrEmpty(plId))
            {
                plId = ExtractPlaylistIdFromContext(obj);
            }

            if (string.IsNullOrWhiteSpace(plId)) return null;

            string subtitle = ReadText(obj, "subtitle");
            string thumbUrl = ExtractBestThumbnailUrl(obj);
            string coverVidId = ExtractVideoIdFromThumbUrl(thumbUrl);

            return new TrackItem
            {
                Id = !string.IsNullOrEmpty(coverVidId) ? coverVidId : ("PL_" + plId),
                PlaylistId = plId,
                Title = title,
                Artist = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : "Playlist • YT Music",
                Duration = "PLAYLIST",
                ItemType = "playlist",
                CustomThumbUrl = thumbUrl
            };
        }

        private static void WalkForMusicPlaylistTracks(JsonElement el, List<TrackItem> items, HashSet<string> seenIds, ref string continuation)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name;
                    var val = prop.Value;

                    // Nunca desce em prateleiras de sugestões/recomendações
                    if (name == "musicShelfRenderer" || name == "musicCarouselShelfRenderer")
                        continue;

                    if (name == "musicResponsiveListItemRenderer")
                    {
                        var track = ParseMusicResponsiveTrack(val);
                        if (track != null && seenIds.Add(track.Id))
                        {
                            items.Add(track);
                        }
                        continue;
                    }

                    if ((name == "continuationItemRenderer" || name == "nextContinuationData") && string.IsNullOrEmpty(continuation))
                    {
                        string tok = name == "nextContinuationData"
                            ? ReadString(val, "continuation")
                            : ExtractContinuationFromItem(val);
                        if (!string.IsNullOrEmpty(tok))
                            continuation = tok;
                        continue;
                    }

                    WalkForMusicPlaylistTracks(val, items, seenIds, ref continuation);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    WalkForMusicPlaylistTracks(child, items, seenIds, ref continuation);
                }
            }
        }

        private static TrackItem? ParseMusicResponsiveTrack(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            string vidId = "";
            if (obj.TryGetProperty("playlistItemData", out var pidData))
            {
                vidId = ReadString(pidData, "videoId");
            }
            if (string.IsNullOrEmpty(vidId))
            {
                vidId = ExtractFirstVideoIdRecursive(obj);
            }
            if (string.IsNullOrEmpty(vidId) || vidId.Length != 11) return null;

            string title = "";
            string artist = "";

            if (obj.TryGetProperty("flexColumns", out var flexCols) && flexCols.ValueKind == JsonValueKind.Array)
            {
                int colIdx = 0;
                foreach (var col in flexCols.EnumerateArray())
                {
                    if (col.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var fcr))
                    {
                        string text = ReadText(fcr, "text");
                        if (colIdx == 0 && !string.IsNullOrWhiteSpace(text))
                            title = text;
                        else if (colIdx == 1 && !string.IsNullOrWhiteSpace(text))
                            artist = text;
                    }
                    colIdx++;
                }
            }

            if (string.IsNullOrWhiteSpace(title)) return null;
            if (string.IsNullOrWhiteSpace(artist)) artist = "YouTube Music";

            string durText = "";
            if (obj.TryGetProperty("fixedColumns", out var fixedCols) && fixedCols.ValueKind == JsonValueKind.Array)
            {
                foreach (var col in fixedCols.EnumerateArray())
                {
                    if (col.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out var fxr))
                    {
                        string text = ReadText(fxr, "text");
                        if (!string.IsNullOrWhiteSpace(text) && text.Contains(':'))
                        {
                            durText = text.Trim();
                            break;
                        }
                    }
                }
            }

            int durSec = YouTubeService.ParseDurationToSeconds(durText);
            string thumbUrl = ExtractBestThumbnailUrl(obj);
            if (string.IsNullOrWhiteSpace(thumbUrl))
            {
                thumbUrl = "https://i.ytimg.com/vi/" + vidId + "/mqdefault.jpg";
            }

            return new TrackItem
            {
                Id = vidId,
                Title = title,
                Artist = artist,
                Duration = string.IsNullOrEmpty(durText) ? "--:--" : durText,
                DurationSeconds = durSec,
                CustomThumbUrl = thumbUrl,
                ItemType = "video"
            };
        }

        private static string ExtractFirstVideoIdRecursive(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("videoId", out var vEl) && vEl.ValueKind == JsonValueKind.String)
                {
                    string v = vEl.GetString() ?? "";
                    if (v.Length == 11) return v;
                }
                foreach (var p in el.EnumerateObject())
                {
                    string found = ExtractFirstVideoIdRecursive(p.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string found = ExtractFirstVideoIdRecursive(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return string.Empty;
        }

        // =========================================================================
        // HELPERS JSON
        // =========================================================================

        private static string ReadString(JsonElement obj, string propName)
        {
            if (obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty(propName, out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        private static string ReadText(JsonElement obj, string propName)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propName, out var el))
                return string.Empty;

            if (el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? string.Empty;

            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
                    return st.GetString() ?? string.Empty;

                if (el.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
                    return ct.GetString() ?? string.Empty;

                if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var r in runs.EnumerateArray())
                    {
                        if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.Append(t.GetString());
                    }
                    return sb.ToString().Trim();
                }
            }

            return string.Empty;
        }

        private static string ExtractContinuationFromItem(JsonElement itemEl)
        {
            try
            {
                if (itemEl.TryGetProperty("continuationEndpoint", out var ep) &&
                    ep.TryGetProperty("continuationCommand", out var cmd) &&
                    cmd.TryGetProperty("token", out var tok) &&
                    tok.ValueKind == JsonValueKind.String)
                {
                    return tok.GetString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string ExtractBrowseIdFromOwner(JsonElement obj, string ownerProp)
        {
            try
            {
                if (!obj.TryGetProperty(ownerProp, out var ownerEl) ||
                    !ownerEl.TryGetProperty("runs", out var runs) ||
                    runs.ValueKind != JsonValueKind.Array)
                    return string.Empty;

                foreach (var r in runs.EnumerateArray())
                {
                    if (r.TryGetProperty("navigationEndpoint", out var nav) &&
                        nav.TryGetProperty("browseEndpoint", out var be) &&
                        be.TryGetProperty("browseId", out var bid) &&
                        bid.ValueKind == JsonValueKind.String)
                    {
                        string id = bid.GetString() ?? "";
                        if (id.StartsWith("UC", StringComparison.Ordinal)) return id;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string ExtractClassicChannelAvatar(JsonElement obj)
        {
            try
            {
                if (obj.TryGetProperty("channelThumbnailSupportedRenderers", out var c1))
                {
                    string u = ExtractBestThumbnailUrl(c1);
                    if (!string.IsNullOrEmpty(u)) return YouTubeService.NormalizeAvatarUrl(u);
                }
                if (obj.TryGetProperty("channelThumbnail", out var c2))
                {
                    string u = ExtractBestThumbnailUrl(c2);
                    if (!string.IsNullOrEmpty(u)) return YouTubeService.NormalizeAvatarUrl(u);
                }
                if (obj.TryGetProperty("avatar", out var c3))
                {
                    string u = ExtractNestedImageUrl(c3);
                    if (!string.IsNullOrEmpty(u)) return YouTubeService.NormalizeAvatarUrl(u);
                }
            }
            catch { }
            return string.Empty;
        }

        private static string ExtractBestThumbnailUrl(JsonElement el)
        {
            string best = string.Empty;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("thumbnails", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        string u = ReadString(t, "url");
                        if (!string.IsNullOrEmpty(u)) best = u;
                    }
                    if (!string.IsNullOrEmpty(best)) return best.StartsWith("//") ? "https:" + best : best;
                }
                foreach (var p in el.EnumerateObject())
                {
                    string nested = ExtractBestThumbnailUrl(p.Value);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string nested = ExtractBestThumbnailUrl(item);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            return best;
        }

        private static string ExtractNestedImageUrl(JsonElement el)
        {
            string best = string.Empty;
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sources.EnumerateArray())
                    {
                        string u = ReadString(s, "url");
                        if (!string.IsNullOrEmpty(u)) best = u;
                    }
                    if (!string.IsNullOrEmpty(best)) return best.StartsWith("//") ? "https:" + best : best;
                }
                if (el.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in thumbs.EnumerateArray())
                    {
                        string u = ReadString(t, "url");
                        if (!string.IsNullOrEmpty(u)) best = u;
                    }
                    if (!string.IsNullOrEmpty(best)) return best.StartsWith("//") ? "https:" + best : best;
                }
                foreach (var p in el.EnumerateObject())
                {
                    string nested = ExtractNestedImageUrl(p.Value);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string nested = ExtractNestedImageUrl(item);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
            return best;
        }

        private static string ExtractFirstChannelBrowseId(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("browseId", out var bid) && bid.ValueKind == JsonValueKind.String)
                {
                    string id = bid.GetString() ?? "";
                    if (id.StartsWith("UC", StringComparison.Ordinal)) return id;
                }
                foreach (var p in el.EnumerateObject())
                {
                    string found = ExtractFirstChannelBrowseId(p.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string found = ExtractFirstChannelBrowseId(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return string.Empty;
        }

        private static string ExtractPlaylistIdFromContext(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("playlistId", out var pid) && pid.ValueKind == JsonValueKind.String)
                {
                    string id = pid.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
                if (el.TryGetProperty("browseId", out var bid) && bid.ValueKind == JsonValueKind.String)
                {
                    string id = bid.GetString() ?? "";
                    if (id.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && id.Length > 2)
                        return id.Substring(2);
                }
                foreach (var p in el.EnumerateObject())
                {
                    string found = ExtractPlaylistIdFromContext(p.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string found = ExtractPlaylistIdFromContext(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return string.Empty;
        }

        private static string ExtractLockupBadgeText(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("thumbnailBadgeViewModel", out var badge))
                {
                    string t = ReadString(badge, "text");
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name == "metadata") continue;
                    string found = ExtractLockupBadgeText(p.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    string found = ExtractLockupBadgeText(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return string.Empty;
        }

        private static string ExtractVideoIdFromThumbUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            var m = _rxVideoIdFromUrl.Match(url);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static bool IsLiveBadgePresent(JsonElement obj)
        {
            if (!obj.TryGetProperty("badges", out var badges) || badges.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var b in badges.EnumerateArray())
            {
                if (b.TryGetProperty("metadataBadgeRenderer", out var mbr))
                {
                    string style = ReadString(mbr, "style");
                    string label = ReadString(mbr, "label");
                    if (style.IndexOf("LIVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        label.IndexOf("AO VIVO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(label, "LIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasShortsNavigationEndpoint(JsonElement obj)
        {
            return obj.TryGetProperty("navigationEndpoint", out var nav) &&
                   nav.TryGetProperty("reelWatchEndpoint", out _);
        }

        private static void ExtractViewsAndDateFromRuns(JsonElement viEl, ref string viewsText, ref string publishedText)
        {
            if (!viEl.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                return;

            foreach (var r in runs.EnumerateArray())
            {
                string t = ReadString(r, "text").Trim();
                if (string.IsNullOrEmpty(t) || t == "•" || t == " · ") continue;
                if (string.IsNullOrEmpty(viewsText) && LooksLikeViews(t))
                    viewsText = t;
                else if (string.IsNullOrEmpty(publishedText) && LooksLikeDate(t))
                    publishedText = t;
            }
        }

        private static bool LooksLikeViews(string text)
        {
            return text.IndexOf("visualiza", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("assistindo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("watching", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeDate(string text)
        {
            return text.StartsWith("há ", StringComparison.OrdinalIgnoreCase) ||
                   text.IndexOf("ago", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("transmitido", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("streamed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("estreia", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

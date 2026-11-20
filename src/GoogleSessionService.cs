#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    public sealed class GoogleAccountProfile
    {
        public bool IsAuthenticated { get; set; }
        public bool SessionExpired { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Handle { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
    }

    /// <summary>
    /// Camada 4 — Autenticação & Privacidade.
    /// Centraliza o acesso seguro aos cookies criptografados (CookieVault / DPAPI),
    /// a assinatura SAPISIDHASH para requisições autenticadas do YouTube e YouTube Music,
    /// e a leitura em cache do perfil da conta conectada.
    /// </summary>
    public sealed class GoogleSessionService
    {
        public const string WebClientVersion = "2.20250501.01.00";
        public const string MusicClientVersion = "1.20250501.01.00";

        public static GoogleSessionService Shared { get; } = new GoogleSessionService();

        private readonly object _lock = new object();
        private string? _cachedCookieHeader;
        private string? _cachedCookieSourcePath;
        private DateTime _cachedCookieFileWriteUtc = DateTime.MinValue;
        private DateTime _cachedCookieExpiresUtc = DateTime.MinValue;

        private GoogleAccountProfile? _cachedProfile;
        private DateTime _cachedProfileExpiresUtc = DateTime.MinValue;

        private bool _sessionExpiredFlag;

        public bool IsSessionExpired
        {
            get { lock (_lock) return _sessionExpiredFlag; }
        }

        public void MarkSessionExpired()
        {
            lock (_lock)
            {
                _sessionExpiredFlag = true;
                if (_cachedProfile != null)
                {
                    _cachedProfile.IsAuthenticated = false;
                    _cachedProfile.SessionExpired = true;
                }
            }
        }

        public void InvalidateCache()
        {
            lock (_lock)
            {
                _cachedCookieHeader = null;
                _cachedCookieSourcePath = null;
                _cachedCookieFileWriteUtc = DateTime.MinValue;
                _cachedCookieExpiresUtc = DateTime.MinValue;
                _cachedProfile = null;
                _cachedProfileExpiresUtc = DateTime.MinValue;
                _sessionExpiredFlag = false;
            }
        }

        public bool HasStoredSession(AppConfig? config)
        {
            if (config == null) return false;
            if (CookieVault.HasStoredCookies(config.CookiesFilePath))
                return true;

            return !string.IsNullOrWhiteSpace(config.BrowserForCookies) &&
                   !string.Equals(config.BrowserForCookies, "none", StringComparison.OrdinalIgnoreCase);
        }

        public string? ResolveCookieHeader(AppConfig? config)
        {
            if (config == null) return null;

            try
            {
                string? sourcePath = CookieVault.ResolveEffectiveCookiePath(config.CookiesFilePath);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    return null;

                DateTime writeUtc = File.GetLastWriteTimeUtc(sourcePath);

                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_cachedCookieHeader) &&
                        string.Equals(_cachedCookieSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                        _cachedCookieFileWriteUtc == writeUtc &&
                        DateTime.UtcNow < _cachedCookieExpiresUtc)
                    {
                        return _cachedCookieHeader;
                    }
                }

                string plaintext = CookieVault.ReadCookiesPlaintext(sourcePath);
                string? header = CookieVault.BuildCookieHeaderFromNetscapeContent(plaintext);
                if (string.IsNullOrEmpty(header))
                    return null;

                lock (_lock)
                {
                    _cachedCookieHeader = header;
                    _cachedCookieSourcePath = sourcePath;
                    _cachedCookieFileWriteUtc = writeUtc;
                    _cachedCookieExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                }

                return header;
            }
            catch
            {
                return null;
            }
        }

        public Dictionary<string, string> BuildAuthenticatedHeaders(
            AppConfig? config,
            string origin,
            string clientName,
            string clientVersion,
            out bool hasAuthCookies)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Origin", origin },
                { "X-Origin", origin },
                { "X-YouTube-Client-Name", clientName },
                { "X-YouTube-Client-Version", clientVersion }
            };

            string? cookieHeader = ResolveCookieHeader(config);
            hasAuthCookies = false;

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                headers["Cookie"] = cookieHeader;
                string? sapisidHash = BuildSapisidHashHeader(cookieHeader, origin);
                if (!string.IsNullOrEmpty(sapisidHash))
                {
                    headers["Authorization"] = sapisidHash;
                    headers["X-Goog-AuthUser"] = "0";
                    hasAuthCookies = true;
                }
            }

            return headers;
        }

        public Dictionary<string, string> BuildWebHeaders(AppConfig? config, out bool hasAuthCookies)
        {
            return BuildAuthenticatedHeaders(config, "https://www.youtube.com", "1", WebClientVersion, out hasAuthCookies);
        }

        public Dictionary<string, string> BuildMusicHeaders(AppConfig? config, out bool hasAuthCookies)
        {
            return BuildAuthenticatedHeaders(config, "https://music.youtube.com", "67", MusicClientVersion, out hasAuthCookies);
        }

        public static string? BuildSapisidHashHeader(string? cookieHeader, string origin)
        {
            if (string.IsNullOrEmpty(cookieHeader) || string.IsNullOrEmpty(origin))
                return null;

            try
            {
                string? sapisid = ExtractCookieValue(cookieHeader, "SAPISID");
                if (string.IsNullOrEmpty(sapisid))
                    sapisid = ExtractCookieValue(cookieHeader, "__Secure-3PAPISID");
                if (string.IsNullOrEmpty(sapisid))
                    sapisid = ExtractCookieValue(cookieHeader, "__Secure-1PAPISID");

                if (string.IsNullOrEmpty(sapisid))
                    return null;

                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string raw = string.Format("{0} {1} {2}", ts, sapisid, origin);
                byte[] hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(raw));

                var sb = new StringBuilder(hashBytes.Length * 2);
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }

                return string.Format("SAPISIDHASH {0}_{1}", ts, sb);
            }
            catch
            {
                return null;
            }
        }

        public static string? ExtractCookieValue(string? cookieHeader, string cookieName)
        {
            if (string.IsNullOrEmpty(cookieHeader) || string.IsNullOrEmpty(cookieName))
                return null;

            string[] parts = cookieHeader.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;

                string name = part.Substring(0, eq).Trim();
                if (string.Equals(name, cookieName, StringComparison.Ordinal))
                {
                    return part.Substring(eq + 1).Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// Verifica no JSON de resposta do InnerTube se o Google reconheceu a sessão como autenticada
        /// (responseContext.serviceTrackingParams[GFEEDBACK].logged_in == "1").
        /// </summary>
        public static bool? DetectLoggedInState(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("responseContext", out var ctx) ||
                    !ctx.TryGetProperty("serviceTrackingParams", out var services) ||
                    services.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var svc in services.EnumerateArray())
                {
                    if (!svc.TryGetProperty("params", out var pArr) || pArr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var p in pArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("key", out var kEl) &&
                            string.Equals(kEl.GetString(), "logged_in", StringComparison.Ordinal) &&
                            p.TryGetProperty("value", out var vEl))
                        {
                            string? val = vEl.GetString();
                            if (val == "1") return true;
                            if (val == "0") return false;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public async Task<GoogleAccountProfile> GetAccountProfileAsync(AppConfig? config, bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh)
            {
                lock (_lock)
                {
                    if (_cachedProfile != null && DateTime.UtcNow < _cachedProfileExpiresUtc)
                        return _cachedProfile;
                }
            }

            var headers = BuildWebHeaders(config, out bool hasAuthCookies);
            if (!hasAuthCookies)
            {
                var unauth = new GoogleAccountProfile { IsAuthenticated = false, SessionExpired = false };
                lock (_lock)
                {
                    _cachedProfile = unauth;
                    _cachedProfileExpiresUtc = DateTime.UtcNow.AddMinutes(2);
                }
                return unauth;
            }

            try
            {
                string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + WebClientVersion + "\",\"hl\":\"pt\",\"gl\":\"BR\"}}}";
                string json = await HttpPipeline.PostJsonAsync(
                    "https://www.youtube.com/youtubei/v1/account/account_menu?prettyPrint=false",
                    payload,
                    "https://www.youtube.com/",
                    headers,
                    ct).ConfigureAwait(false);

                var profile = ParseAccountMenuJson(json);
                lock (_lock)
                {
                    _cachedProfile = profile;
                    _cachedProfileExpiresUtc = DateTime.UtcNow.AddMinutes(15);
                    _sessionExpiredFlag = profile.SessionExpired;
                }
                return profile;
            }
            catch (Exception ex)
            {
                Logger.Warn("AUTH", "Não foi possível consultar perfil da conta Google: " + ex.Message);
                return new GoogleAccountProfile { IsAuthenticated = false, SessionExpired = false };
            }
        }

        internal static GoogleAccountProfile ParseAccountMenuJson(string? json)
        {
            var profile = new GoogleAccountProfile();
            if (string.IsNullOrWhiteSpace(json)) return profile;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool? loggedIn = DetectLoggedInState(root);

                if (TryFindPropertyRecursive(root, "activeAccountHeaderRenderer", out var headerObj))
                {
                    profile.IsAuthenticated = true;
                    profile.SessionExpired = false;
                    profile.DisplayName = ReadTextField(headerObj, "accountName");
                    profile.Handle = ReadTextField(headerObj, "channelHandle");

                    if (headerObj.TryGetProperty("accountPhoto", out var photoEl) &&
                        photoEl.TryGetProperty("thumbnails", out var thumbsEl) &&
                        thumbsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in thumbsEl.EnumerateArray())
                        {
                            if (t.TryGetProperty("url", out var uEl))
                            {
                                string? u = uEl.GetString();
                                if (!string.IsNullOrWhiteSpace(u))
                                {
                                    profile.AvatarUrl = YouTubeService.NormalizeAvatarUrl(u);
                                }
                            }
                        }
                    }
                    return profile;
                }

                if (loggedIn == false)
                {
                    profile.IsAuthenticated = false;
                    profile.SessionExpired = true;
                }
            }
            catch { }

            return profile;
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
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindPropertyRecursive(item, propertyName, out found))
                        return true;
                }
            }

            found = default;
            return false;
        }

        private static string ReadTextField(JsonElement obj, string propName)
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
    }
}

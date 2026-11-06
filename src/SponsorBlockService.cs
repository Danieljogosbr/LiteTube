using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LiteTube
{
    public class SponsorSegment
    {
        public string Category { get; set; }
        public string ActionType { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string UUID { get; set; }

        public double StartTime
        {
            get { return Start; }
            set { Start = value; }
        }

        public double EndTime
        {
            get { return End; }
            set { End = value; }
        }

        public Color CategoryColor
        {
            get
            {
                switch ((Category ?? "").ToLowerInvariant())
                {
                    case "sponsor":
                        return Color.FromArgb(0, 214, 0); // #00D600 Verde (Patrocínios)
                    case "intro":
                        return Color.FromArgb(0, 160, 255); // #00A0FF Azul (Aberturas)
                    case "outro":
                        return Color.FromArgb(184, 0, 255); // #B800FF Roxo (Encerramentos)
                    case "selfpromo":
                    case "interaction":
                        return Color.FromArgb(255, 216, 0); // #FFD800 Amarelo (Auto-promoção e inscrições)
                    case "preview":
                        return Color.FromArgb(0, 160, 255); // #00A0FF
                    case "music_offtopic":
                        return Color.FromArgb(62, 166, 255); // #3EA6FF Azul/Cinza (Trecho não-musical)
                    default:
                        return Color.FromArgb(0, 214, 0);
                }
            }
        }

        public string CategoryLabel
        {
            get
            {
                switch ((Category ?? "").ToLowerInvariant())
                {
                    case "sponsor": return "Patrocínio";
                    case "selfpromo": return "Auto-promoção";
                    case "interaction": return "Interação / Inscrição";
                    case "intro": return "Abertura / Intro";
                    case "outro": return "Encerramento / Outro";
                    case "preview": return "Prévia";
                    case "music_offtopic": return "Introdução / Trecho não-musical";
                    default: return "Patrocínio";
                }
            }
        }
    }

    public static class SponsorBlockService
    {
        public const string VideoCategoriesJson = "[\"sponsor\",\"intro\",\"outro\",\"selfpromo\",\"interaction\"]";
        public const string MusicCategoriesJson = "[\"music_offtopic\"]";

        private static readonly Dictionary<string, List<SponsorSegment>> _cache =
            new Dictionary<string, List<SponsorSegment>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static readonly HttpClient _httpClient;

        static SponsorBlockService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(4.5)
            };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiteTube/2.0");
        }

        public static Task<List<SponsorSegment>> GetSegmentsAsync(string videoId)
        {
            return GetSegmentsAsync(videoId, VideoCategoriesJson);
        }

        public static Task<List<SponsorSegment>> GetVideoSegmentsAsync(string videoId)
        {
            return GetSegmentsAsync(videoId, VideoCategoriesJson);
        }

        public static Task<List<SponsorSegment>> GetMusicSegmentsAsync(string videoId)
        {
            return GetSegmentsAsync(videoId, MusicCategoriesJson);
        }

        public static async Task<List<SponsorSegment>> GetSegmentsAsync(string videoId, string categoriesJson)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return new List<SponsorSegment>();
            if (string.IsNullOrWhiteSpace(categoriesJson)) categoriesJson = VideoCategoriesJson;

            string cacheKey = videoId.Trim() + "|" + categoriesJson;
            lock (_cacheLock)
            {
                List<SponsorSegment> cached;
                if (_cache.TryGetValue(cacheKey, out cached))
                {
                    return cached;
                }
            }

            try
            {
                string escapedCategories = Uri.EscapeDataString(categoriesJson);
                string url = string.Format("https://sponsor.ajay.app/api/skipSegments?videoID={0}&categories={1}",
                    Uri.EscapeDataString(videoId.Trim()), escapedCategories);

                using (HttpResponseMessage resp = await _httpClient.GetAsync(url).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        var empty = new List<SponsorSegment>();
                        lock (_cacheLock)
                        {
                            _cache[cacheKey] = empty;
                        }
                        Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: Nenhum segmento cadastrado para {0} (404)", videoId));
                        return empty;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        Logger.Warn("SPONSORBLOCK", string.Format("SponsorBlock HTTP {0} para {1}", (int)resp.StatusCode, videoId));
                        return new List<SponsorSegment>();
                    }

                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var segments = ParseSegmentsJson(json);

                    lock (_cacheLock)
                    {
                        _cache[cacheKey] = segments;
                    }

                    Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: {0} segmentos encontrados para {1}", segments.Count, videoId));
                    return segments;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("SPONSORBLOCK", "Falha ao consultar SponsorBlock via HttpClient: " + ex.Message);
                return new List<SponsorSegment>();
            }
        }

        public static bool CheckAndGetSkipTarget(double currentSeconds, List<SponsorSegment> segments, out double skipTo)
        {
            SponsorSegment matched;
            return CheckAndGetSkipTarget(currentSeconds, segments, out skipTo, out matched);
        }

        public static bool CheckAndGetSkipTarget(double currentSeconds, List<SponsorSegment> segments, out double skipTo, out SponsorSegment matchedSegment)
        {
            skipTo = 0.0;
            matchedSegment = null;
            if (segments == null || segments.Count == 0) return false;

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg == null) continue;
                if (!string.IsNullOrEmpty(seg.ActionType) && !string.Equals(seg.ActionType, "skip", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seg.End > seg.Start && currentSeconds >= seg.Start && currentSeconds < seg.End - 0.35)
                {
                    skipTo = seg.End + 0.1;
                    matchedSegment = seg;
                    return true;
                }
            }
            return false;
        }

        public static List<SponsorSegment> ParseSegmentsJson(string json)
        {
            var segments = new List<SponsorSegment>();
            if (string.IsNullOrWhiteSpace(json)) return segments;

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string cat = el.TryGetProperty("category", out var cp) && cp.ValueKind == JsonValueKind.String ? (cp.GetString() ?? "sponsor") : "sponsor";
                            string action = el.TryGetProperty("actionType", out var ap) && ap.ValueKind == JsonValueKind.String ? (ap.GetString() ?? "skip") : "skip";
                            string uuid = el.TryGetProperty("UUID", out var up) && up.ValueKind == JsonValueKind.String ? (up.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N");

                            double start = 0;
                            double end = 0;

                            if (el.TryGetProperty("segment", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() >= 2)
                            {
                                start = sp[0].GetDouble();
                                end = sp[1].GetDouble();
                            }

                            if (end > start)
                            {
                                segments.Add(new SponsorSegment
                                {
                                    Category = cat,
                                    ActionType = action,
                                    Start = start,
                                    End = end,
                                    UUID = uuid
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            return segments;
        }
    }
}

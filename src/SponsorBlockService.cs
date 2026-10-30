using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiteTube
{
    public class SponsorSegment
    {
        public string Category { get; set; }
        public string ActionType { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public string UUID { get; set; }

        public Color CategoryColor
        {
            get
            {
                switch ((Category ?? "").ToLowerInvariant())
                {
                    case "sponsor":
                        return Color.FromArgb(0, 212, 130); // Vibrant Green
                    case "selfpromo":
                        return Color.FromArgb(255, 180, 0); // Vibrant Gold
                    case "interaction":
                        return Color.FromArgb(255, 140, 0); // Orange
                    case "intro":
                        return Color.FromArgb(0, 200, 255); // Cyan
                    case "outro":
                        return Color.FromArgb(155, 89, 182); // Purple
                    case "preview":
                        return Color.FromArgb(52, 152, 219); // Sky Blue
                    case "music_offtopic":
                        return Color.FromArgb(230, 126, 34); // Amber
                    default:
                        return Color.FromArgb(0, 212, 130);
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
                    case "interaction": return "Interação / Like";
                    case "intro": return "Vinheta / Abertura";
                    case "outro": return "Encerramento";
                    case "preview": return "Prévia";
                    case "music_offtopic": return "Trecho não-musical";
                    default: return "Patrocínio";
                }
            }
        }
    }

    public static class SponsorBlockService
    {
        private static readonly Dictionary<string, List<SponsorSegment>> _cache = new Dictionary<string, List<SponsorSegment>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        public static async Task<List<SponsorSegment>> GetSegmentsAsync(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return new List<SponsorSegment>();

            lock (_cacheLock)
            {
                List<SponsorSegment> cached;
                if (_cache.TryGetValue(videoId, out cached))
                {
                    return cached;
                }
            }

            return await Task.Run(() =>
            {
                try
                {
                    string categories = Uri.EscapeDataString("[\"sponsor\",\"selfpromo\",\"interaction\",\"intro\",\"outro\",\"preview\",\"music_offtopic\"]");
                    string url = string.Format("https://sponsor.ajay.app/api/skipSegments?videoID={0}&categories={1}", Uri.EscapeDataString(videoId), categories);

                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiteTube/1.0";
                    req.Timeout = 4000;
                    req.ReadWriteTimeout = 4000;

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        if (string.IsNullOrEmpty(json)) return new List<SponsorSegment>();

                        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var rawList = serializer.Deserialize<object[]>(json);
                        var segments = new List<SponsorSegment>();

                        if (rawList != null)
                        {
                            for (int i = 0; i < rawList.Length; i++)
                            {
                                var dict = rawList[i] as Dictionary<string, object>;
                                if (dict == null) continue;

                                string cat = dict.ContainsKey("category") ? dict["category"] as string : "sponsor";
                                string action = dict.ContainsKey("actionType") ? dict["actionType"] as string : "skip";
                                string uuid = dict.ContainsKey("UUID") ? dict["UUID"] as string : Guid.NewGuid().ToString("N");

                                double start = 0;
                                double end = 0;

                                if (dict.ContainsKey("segment"))
                                {
                                    var segArr = dict["segment"] as object[];
                                    if (segArr != null && segArr.Length >= 2)
                                    {
                                        start = Convert.ToDouble(segArr[0], System.Globalization.CultureInfo.InvariantCulture);
                                        end = Convert.ToDouble(segArr[1], System.Globalization.CultureInfo.InvariantCulture);
                                    }
                                }

                                if (end > start)
                                {
                                    segments.Add(new SponsorSegment
                                    {
                                        Category = cat,
                                        ActionType = action,
                                        StartTime = start,
                                        EndTime = end,
                                        UUID = uuid
                                    });
                                }
                            }
                        }

                        lock (_cacheLock)
                        {
                            _cache[videoId] = segments;
                        }

                        Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: {0} segmentos encontrados para o vídeo {1}", segments.Count, videoId));
                        return segments;
                    }
                }
                catch (WebException wex)
                {
                    // 404 Not Found is standard for videos with no sponsor submissions
                    var httpResp = wex.Response as HttpWebResponse;
                    if (httpResp != null && httpResp.StatusCode == HttpStatusCode.NotFound)
                    {
                        lock (_cacheLock)
                        {
                            _cache[videoId] = new List<SponsorSegment>();
                        }
                        Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: Nenhum segmento cadastrado para o vídeo {0} (404)", videoId));
                        return new List<SponsorSegment>();
                    }

                    Logger.Warn("SPONSORBLOCK", "Falha ao consultar SponsorBlock: " + wex.Message);
                    return new List<SponsorSegment>();
                }
                catch (Exception ex)
                {
                    Logger.Warn("SPONSORBLOCK", "Erro genérico no SponsorBlock: " + ex.Message);
                    return new List<SponsorSegment>();
                }
            });
        }
    }
}

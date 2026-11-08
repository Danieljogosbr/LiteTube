using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiteTube
{
    /// <summary>
    /// Serviço de extração, download em background e recorte em memória (CroppedBitmap)
    /// de Sprite Sheets (Storyboards) do YouTube para exibição de miniaturas instantâneas
    /// durante o scrubbing (hover/drag) na barra de progresso.
    /// </summary>
    public sealed class StoryboardData
    {
        public string VideoId { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public int LevelIndex { get; init; }
        public int FrameWidth { get; init; }
        public int FrameHeight { get; init; }
        public int FrameCount { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
        public int IntervalMs { get; init; }
        public string NamePattern { get; init; } = "M$M";
        public string Sigh { get; init; } = "";
        public double VideoDurationSeconds { get; set; }

        private readonly ConcurrentDictionary<int, BitmapSource> _sheets = new();
        private readonly ConcurrentDictionary<int, byte> _loadingSheets = new();
        private readonly ConcurrentDictionary<int, BitmapSource> _croppedFrames = new();
        private BitmapSource? _fallbackThumbnail;

        public int FramesPerSheet => Math.Max(1, Columns * Rows);

        public string GetSheetUrl(int sheetIndex)
        {
            string name = (NamePattern ?? "M$M").Replace("$M", sheetIndex.ToString());
            string url = BaseUrl
                .Replace("$L", LevelIndex.ToString())
                .Replace("$N", name);

            if (!string.IsNullOrEmpty(Sigh))
            {
                url += (url.Contains('?') ? "&" : "?") + "sigh=" + Sigh;
            }

            return url;
        }

        public int GetFrameIndex(double targetSeconds, double totalDurationSeconds)
        {
            if (FrameCount <= 0) return 0;

            double effTotal = totalDurationSeconds > 0 ? totalDurationSeconds : VideoDurationSeconds;
            int frameIdx;

            if (IntervalMs > 0)
            {
                frameIdx = (int)Math.Floor((Math.Max(0.0, targetSeconds) * 1000.0) / IntervalMs);
            }
            else if (effTotal > 0)
            {
                double ratio = Math.Max(0.0, Math.Min(1.0, targetSeconds / effTotal));
                frameIdx = (int)Math.Floor(ratio * FrameCount);
            }
            else
            {
                frameIdx = (int)Math.Floor(Math.Max(0.0, targetSeconds) / 2.0);
            }

            return Math.Max(0, Math.Min(FrameCount - 1, frameIdx));
        }

        public void SetSheetBitmap(int sheetIndex, BitmapSource bitmap)
        {
            if (bitmap != null)
            {
                _sheets[sheetIndex] = bitmap;
            }
        }

        public void SetFallbackThumbnail(BitmapSource? bitmap)
        {
            _fallbackThumbnail = bitmap;
        }

        public BitmapSource? TryGetFrame(double targetSeconds, double totalDurationSeconds, Action? onSheetReadyCallback = null)
        {
            if (FrameCount <= 0 || string.IsNullOrEmpty(BaseUrl))
            {
                return _fallbackThumbnail;
            }

            int frameIndex = GetFrameIndex(targetSeconds, totalDurationSeconds);
            if (_croppedFrames.TryGetValue(frameIndex, out var cachedCrop))
            {
                return cachedCrop;
            }

            int sheetIndex = frameIndex / FramesPerSheet;
            if (_sheets.TryGetValue(sheetIndex, out var sheetBitmap))
            {
                var cropped = CropFrameFromSheet(sheetBitmap, frameIndex);
                if (cropped != null)
                {
                    _croppedFrames[frameIndex] = cropped;
                    return cropped;
                }
            }

            // Dispara o download assíncrono da sprite sheet necessária caso ainda não esteja em RAM
            TriggerSheetDownload(sheetIndex, onSheetReadyCallback);

            return _fallbackThumbnail;
        }

        public void TriggerSheetDownload(int sheetIndex, Action? onSheetReadyCallback = null)
        {
            if (sheetIndex < 0 || _sheets.ContainsKey(sheetIndex) || string.IsNullOrEmpty(BaseUrl))
                return;

            if (!_loadingSheets.TryAdd(sheetIndex, 1))
                return;

            Task.Run(async () =>
            {
                try
                {
                    string url = GetSheetUrl(sheetIndex);
                    byte[]? bytes = await HttpPipeline.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var bmp = StoryboardService.DecodeFrozenBitmap(bytes, 0);
                        if (bmp != null)
                        {
                            _sheets[sheetIndex] = bmp;
                            onSheetReadyCallback?.Invoke();
                        }
                    }
                }
                catch { }
                finally
                {
                    _loadingSheets.TryRemove(sheetIndex, out byte _);
                }
            });
        }

        private BitmapSource? CropFrameFromSheet(BitmapSource sheet, int frameIndex)
        {
            try
            {
                int localIndex = frameIndex % FramesPerSheet;
                int row = localIndex / Math.Max(1, Columns);
                int col = localIndex % Math.Max(1, Columns);

                int cellW = FrameWidth > 0 ? FrameWidth : (sheet.PixelWidth / Math.Max(1, Columns));
                int cellH = FrameHeight > 0 ? FrameHeight : (sheet.PixelHeight / Math.Max(1, Rows));

                int x = col * cellW;
                int y = row * cellH;

                if (x + cellW > sheet.PixelWidth) cellW = sheet.PixelWidth - x;
                if (y + cellH > sheet.PixelHeight) cellH = sheet.PixelHeight - y;

                if (cellW <= 0 || cellH <= 0 || x < 0 || y < 0)
                    return _fallbackThumbnail;

                var cropped = new CroppedBitmap(sheet, new Int32Rect(x, y, cellW, cellH));
                cropped.Freeze();
                return cropped;
            }
            catch
            {
                return _fallbackThumbnail;
            }
        }
    }

    public static class StoryboardService
    {
        private static readonly ConcurrentDictionary<string, StoryboardData> _cache = new(StringComparer.Ordinal);
        private static readonly Regex _specRegex = new(
            "\"playerStoryboardSpecRenderer\"\\s*:\\s*\\{[^}]*?\"spec\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static async Task<StoryboardData?> GetStoryboardAsync(string videoId, double durationSeconds = 0.0, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return null;

            if (_cache.TryGetValue(videoId, out var existing))
            {
                if (durationSeconds > 0 && existing.VideoDurationSeconds <= 0)
                    existing.VideoDurationSeconds = durationSeconds;
                return existing;
            }

            return await Task.Run(async () =>
            {
                StoryboardData? data = null;
                try
                {
                    string payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240530.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"videoId\":\"" + videoId + "\"}";
                    string json = await HttpPipeline.PostJsonAsync(
                        "https://www.youtube.com/youtubei/v1/player?prettyPrint=false",
                        payload,
                        ct: cancellationToken).ConfigureAwait(false);

                    string? rawSpec = ExtractSpecFromJson(json);
                    if (string.IsNullOrEmpty(rawSpec))
                    {
                        string watchHtml = await HttpPipeline.GetStringAsync(
                            "https://www.youtube.com/watch?v=" + videoId + "&hl=pt&gl=BR",
                            ct: cancellationToken).ConfigureAwait(false);
                        var m = _specRegex.Match(watchHtml);
                        if (m.Success)
                        {
                            rawSpec = Regex.Unescape(m.Groups[1].Value);
                        }
                    }

                    if (!string.IsNullOrEmpty(rawSpec))
                    {
                        data = ParseSpec(videoId, rawSpec, durationSeconds);
                    }
                }
                catch { }

                if (data == null)
                {
                    data = new StoryboardData
                    {
                        VideoId = videoId,
                        VideoDurationSeconds = durationSeconds
                    };
                }

                // Pré-carrega a miniatura base de fallback e a primeira folha (M0) em background
                try
                {
                    string thumbUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";
                    byte[]? thumbBytes = await HttpPipeline.GetByteArrayAsync(thumbUrl, cancellationToken).ConfigureAwait(false);
                    if (thumbBytes != null && thumbBytes.Length > 0)
                    {
                        var fallbackBmp = DecodeFrozenBitmap(thumbBytes, 160);
                        data.SetFallbackThumbnail(fallbackBmp);
                    }
                }
                catch { }

                if (data.FrameCount > 0 && !string.IsNullOrEmpty(data.BaseUrl))
                {
                    data.TriggerSheetDownload(0);
                    Logger.Info("STORYBOARD", string.Format(
                        "Storyboard carregado para {0}: Level={1}, {2}x{3}, Frames={4}, Grid={5}x{6}, Interval={7}ms",
                        videoId, data.LevelIndex, data.FrameWidth, data.FrameHeight, data.FrameCount, data.Columns, data.Rows, data.IntervalMs));
                }

                // Limita o cache de storyboards em RAM aos últimos 6 vídeos
                if (_cache.Count >= 6)
                {
                    _cache.Clear();
                }
                _cache[videoId] = data;
                return data;
            }, cancellationToken).ConfigureAwait(false);
        }

        private static string? ExtractSpecFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("storyboards", out var sbElem) &&
                    sbElem.TryGetProperty("playerStoryboardSpecRenderer", out var rendererElem) &&
                    rendererElem.TryGetProperty("spec", out var specElem))
                {
                    return specElem.GetString();
                }
            }
            catch
            {
                var m = _specRegex.Match(json);
                if (m.Success)
                {
                    return Regex.Unescape(m.Groups[1].Value);
                }
            }

            return null;
        }

        public static StoryboardData? ParseSpec(string videoId, string spec, double durationSeconds = 0.0)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;

            string[] parts = spec.Split('|');
            if (parts.Length < 2) return null;

            string baseUrl = parts[0];
            StoryboardData? bestCandidate = null;

            // Níveis vão de L=0 até parts.Length - 2. Preferimos 160x90 (ou o maior disponível até 320px).
            for (int i = 1; i < parts.Length; i++)
            {
                int levelIndex = i - 1;
                string[] fields = parts[i].Split('#');
                if (fields.Length < 8) continue;

                if (!int.TryParse(fields[0], out int w) ||
                    !int.TryParse(fields[1], out int h) ||
                    !int.TryParse(fields[2], out int count) ||
                    !int.TryParse(fields[3], out int cols) ||
                    !int.TryParse(fields[4], out int rows) ||
                    !int.TryParse(fields[5], out int intervalMs))
                {
                    continue;
                }

                string namePattern = fields[6];
                string sigh = fields[7];

                var candidate = new StoryboardData
                {
                    VideoId = videoId,
                    BaseUrl = baseUrl,
                    LevelIndex = levelIndex,
                    FrameWidth = w,
                    FrameHeight = h,
                    FrameCount = count,
                    Columns = cols,
                    Rows = rows,
                    IntervalMs = intervalMs,
                    NamePattern = namePattern,
                    Sigh = sigh,
                    VideoDurationSeconds = durationSeconds
                };

                bestCandidate = candidate;
                if (w >= 160 && w <= 240)
                {
                    break;
                }
            }

            return bestCandidate;
        }

        internal static BitmapSource? DecodeFrozenBitmap(byte[] bytes, int decodePixelWidth)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodePixelWidth > 0)
                {
                    bmp.DecodePixelWidth = decodePixelWidth;
                }
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}

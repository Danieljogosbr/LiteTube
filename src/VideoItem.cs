using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiteTube
{
    public class VideoItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public long DurationSeconds { get; set; }

        // Novos campos de metadados
        public string Description { get; set; } = string.Empty;
        public long ViewCount { get; set; }
        public string UploadDateRaw { get; set; } = string.Empty; // Formato YYYYMMDD ou ISO retornado pelo yt-dlp
        public List<int> AvailableResolutions { get; set; } = new List<int>();

        // Propriedades auxiliares para exibição amigável
        public string FormattedViews => FormatViewCount(ViewCount);
        public string FormattedUploadDate => FormatUploadDate(UploadDateRaw);

        public static string FormatViewCount(long views)
        {
            if (views <= 0)
                return "0 visualizações";
            if (views >= 1_000_000)
                return $"{(views / 1_000_000.0).ToString("0.#", new CultureInfo("pt-BR"))} mi de visualizações";
            if (views >= 1_000)
                return $"{(views / 1_000.0).ToString("0.#", new CultureInfo("pt-BR"))} mil visualizações";
            return $"{views.ToString("N0", new CultureInfo("pt-BR"))} visualizações";
        }

        public static string FormatUploadDate(string rawDate)
        {
            if (string.IsNullOrWhiteSpace(rawDate)) return string.Empty;

            rawDate = rawDate.Trim();

            // Trata padrão YYYYMMDD do yt-dlp
            if (rawDate.Length == 8 && DateTime.TryParseExact(rawDate, "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
            {
                return parsedDate.ToString("dd 'de' MMM. 'de' yyyy", new CultureInfo("pt-BR"));
            }

            if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var generalDate))
            {
                return generalDate.ToString("dd 'de' MMM. 'de' yyyy", new CultureInfo("pt-BR"));
            }

            return rawDate;
        }
    }
}

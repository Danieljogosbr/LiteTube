using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteTube
{
    /// <summary>
    /// Protege e lê cookies locais usando DPAPI nativa do Windows (crypt32.dll - escopo CurrentUser),
    /// evitando armazenamento de cookies de sessão em texto plano no disco.
    /// </summary>
    public static class CookieVault
    {
        private const string DpapiPrefix = "LTDPAPI1:";
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x01;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string GetVaultFilePath()
        {
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, "cookies.dpapi");
        }

        public static string GetLegacyPlaintextFilePath()
        {
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            return Path.Combine(dataDir, "cookies.txt");
        }

        public static string ProtectText(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] protectedBytes = ProtectBytes(plainBytes);
            return DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }

        public static string UnprotectText(string storedContent)
        {
            if (string.IsNullOrEmpty(storedContent)) return string.Empty;
            string trimmed = storedContent.Trim();
            if (!trimmed.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                return storedContent;
            }

            string b64 = trimmed.Substring(DpapiPrefix.Length);
            byte[] cipherBytes = Convert.FromBase64String(b64);
            byte[] plainBytes = UnprotectBytes(cipherBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }

        private static byte[] ProtectBytes(byte[] plainBytes)
        {
            DATA_BLOB inBlob = default;
            DATA_BLOB outBlob = default;
            try
            {
                inBlob.cbData = plainBytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
                Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

                if (!CryptProtectData(ref inBlob, "LiteTubeCookies", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    throw new InvalidOperationException("CryptProtectData falhou com erro Win32 " + Marshal.GetLastWin32Error());
                }

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static byte[] UnprotectBytes(byte[] cipherBytes)
        {
            DATA_BLOB inBlob = default;
            DATA_BLOB outBlob = default;
            try
            {
                inBlob.cbData = cipherBytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(cipherBytes.Length);
                Marshal.Copy(cipherBytes, 0, inBlob.pbData, cipherBytes.Length);

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    throw new InvalidOperationException("CryptUnprotectData falhou com erro Win32 " + Marshal.GetLastWin32Error());
                }

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static string StripBom(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        public static void SaveProtectedCookies(string destinationPath, string netscapePlaintext)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                destinationPath = GetVaultFilePath();

            string dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string clean = StripBom(netscapePlaintext ?? string.Empty);
            string payload = ProtectText(clean);
            string temp = destinationPath + ".tmp";
            File.WriteAllText(temp, payload, new UTF8Encoding(false));
            File.Move(temp, destinationPath, overwrite: true);

            // Remove legado em texto aberto se estiver usando o cofre DPAPI
            string legacyPath = GetLegacyPlaintextFilePath();
            if (!string.Equals(destinationPath, legacyPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
            {
                try { File.Delete(legacyPath); } catch { }
            }
        }

        public static string ReadCookiesPlaintext(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            string raw = StripBom(File.ReadAllText(path, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            if (raw.TrimStart().StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                return StripBom(UnprotectText(raw));
            }

            // Arquivo Netscape em texto plano legado: migra silenciosamente se for data/cookies.txt
            return raw;
        }

        /// <summary>
        /// Se existir data\cookies.txt em texto plano legado, migra para data\cookies.dpapi e apaga o .txt.
        /// </summary>
        public static string MigrateLegacyFileIfPresent(string configuredPath)
        {
            try
            {
                string legacy = GetLegacyPlaintextFilePath();
                string vault = GetVaultFilePath();

                if (string.IsNullOrWhiteSpace(configuredPath))
                {
                    if (File.Exists(vault)) return vault;
                    configuredPath = legacy;
                }

                if (!File.Exists(configuredPath))
                    return configuredPath;

                string raw = StripBom(File.ReadAllText(configuredPath, Encoding.UTF8));
                if (!raw.TrimStart().StartsWith(DpapiPrefix, StringComparison.Ordinal))
                {
                    SaveProtectedCookies(vault, raw);
                    try { if (string.Equals(configuredPath, legacy, StringComparison.OrdinalIgnoreCase)) File.Delete(legacy); } catch { }
                    return vault;
                }
            }
            catch { }
            return configuredPath;
        }

        /// <summary>
        /// Cria um arquivo Netscape temporário apenas durante a execução de um processo externo (yt-dlp).
        /// O chamador deve apagar o arquivo retornado em um bloco finally.
        /// </summary>
        public static string CreateTemporaryPlaintextCookieFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            string plaintext = ReadCookiesPlaintext(sourcePath);
            if (string.IsNullOrWhiteSpace(plaintext))
                return null;

            string tempFile = Path.Combine(Path.GetTempPath(), "litetube_ck_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(tempFile, StripBom(plaintext), new UTF8Encoding(false));
            return tempFile;
        }

        /// <summary>
        /// Remove arquivos temporários de cookies (litetube_ck_*.txt) que possam ter restado no %TEMP%
        /// após fechamento inesperado do aplicativo. Ignora arquivos eventualmente bloqueados por outra instância.
        /// </summary>
        public static int CleanupOrphanedTempCookieFiles()
        {
            int deleted = 0;
            try
            {
                string tempDir = Path.GetTempPath();
                if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
                    return 0;

                foreach (string pattern in new[] { "litetube_ck_*.txt", "litetube_sess_*.txt" })
                {
                    foreach (string file in Directory.EnumerateFiles(tempDir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Delete(file);
                            deleted++;
                        }
                        catch { }
                    }
                }

                string legacyCacheCookie = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "cache", "browser_session_cookies.txt");
                if (File.Exists(legacyCacheCookie))
                {
                    try { File.Delete(legacyCacheCookie); deleted++; } catch { }
                }
            }
            catch { }
            return deleted;
        }

        public static string BuildCookieHeaderFromNetscapeContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            var pairs = new List<string>();
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                string[] parts = line.Split('\t');
                if (parts.Length >= 7)
                {
                    string domain = parts[0];
                    if (domain.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        domain.IndexOf("google.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string name = parts[5].Trim();
                        string val = parts[6].Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            pairs.Add(name + "=" + val);
                        }
                    }
                }
            }

            return pairs.Count > 0 ? string.Join("; ", pairs) : null;
        }

        public static bool LooksLikeNetscapeCookies(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int validRows = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (string.IsNullOrEmpty(l) || l.StartsWith("#")) continue;
                string[] parts = l.Split('\t');
                if (parts.Length >= 7) validRows++;
            }
            return validRows > 0;
        }

        public static string ResolveEffectiveCookiePath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;
            string vault = GetVaultFilePath();
            if (File.Exists(vault)) return vault;
            string legacy = GetLegacyPlaintextFilePath();
            if (File.Exists(legacy)) return legacy;
            return null;
        }

        public static bool HasStoredCookies(string configuredPath)
        {
            return !string.IsNullOrEmpty(ResolveEffectiveCookiePath(configuredPath));
        }

        public static string SanitizeNetscapeContent(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var clean = new List<string>(lines.Length + 1);
            bool hasHeader = false;
            foreach (string rawLine in lines)
            {
                string line = rawLine ?? string.Empty;
                if (line.StartsWith("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
                {
                    hasHeader = true;
                    clean.Add(line);
                    continue;
                }
                if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                {
                    clean.Add(line);
                    continue;
                }
                string trimmed = line.TrimStart();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    clean.Add(line);
                    continue;
                }
                string[] parts = trimmed.Split('\t');
                if (parts.Length >= 7)
                {
                    parts[0] = parts[0].Trim();
                    parts[5] = parts[5].Trim();
                    parts[6] = parts[6].Trim();
                    clean.Add(string.Join("\t", parts));
                }
            }
            if (!hasHeader && clean.Count > 0)
                clean.Insert(0, "# Netscape HTTP Cookie File");
            return string.Join(Environment.NewLine, clean) + Environment.NewLine;
        }

        public static YtDlpCookieScope CreateYtDlpCookieScope(string cookiesFilePath, string browserForCookies)
        {
            return new YtDlpCookieScope(cookiesFilePath, browserForCookies);
        }

        public sealed class YtDlpCookieScope : IDisposable
        {
            private string _tempPlaintextFile;
            public string Arg { get; }
            public bool HasCookies => !string.IsNullOrEmpty(Arg);

            public YtDlpCookieScope(string cookiesFilePath, string browserForCookies)
            {
                string effectivePath = ResolveEffectiveCookiePath(cookiesFilePath);
                if (!string.IsNullOrEmpty(effectivePath))
                {
                    try
                    {
                        string temp = CreateTemporaryPlaintextCookieFile(effectivePath);
                        if (!string.IsNullOrEmpty(temp))
                        {
                            _tempPlaintextFile = temp;
                            Arg = $"--cookies \"{temp}\" ";
                            return;
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(browserForCookies) &&
                    !string.Equals(browserForCookies, "none", StringComparison.OrdinalIgnoreCase))
                {
                    Arg = $"--cookies-from-browser {browserForCookies} ";
                    return;
                }

                Arg = string.Empty;
            }

            public void Dispose()
            {
                string path = _tempPlaintextFile;
                _tempPlaintextFile = null;
                if (!string.IsNullOrEmpty(path))
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
        }
    }
}

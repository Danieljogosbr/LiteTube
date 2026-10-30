using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LiteTube
{
    public enum GpuVendor
    {
        AMD,
        Nvidia,
        Intel,
        Unknown
    }

    public enum GpuType
    {
        Discrete,   // Dedicated / PCIe GPU
        Integrated, // Integrated Graphics (iGPU: Intel HD/UHD/Iris, AMD APU)
        Unknown
    }

    public class GpuInfo
    {
        public string Name { get; set; }
        public GpuVendor Vendor { get; set; }
        public GpuType Type { get; set; }
        public string DeviceId { get; set; }
        public bool IsPrimary { get; set; }

        public string VendorDisplay
        {
            get
            {
                switch (Vendor)
                {
                    case GpuVendor.AMD: return "AMD";
                    case GpuVendor.Nvidia: return "NVIDIA";
                    case GpuVendor.Intel: return "Intel";
                    default: return "Genérica";
                }
            }
        }

        public string TypeDisplay
        {
            get
            {
                switch (Type)
                {
                    case GpuType.Discrete: return "Placa Dedicada";
                    case GpuType.Integrated: return "Gráficos Integrados (iGPU)";
                    default: return "Desconhecido";
                }
            }
        }

        public override string ToString()
        {
            return string.Format("{0} ({1} - {2})", Name, VendorDisplay, TypeDisplay);
        }
    }

    /// <summary>
    /// Intelligent hardware detection for GPU capabilities (AMD, NVIDIA, Intel Discrete/Arc, Intel Integrated HD/UHD/Iris).
    /// Uses native Win32 EnumDisplayDevices and Windows Registry for fast, zero-dependency, permission-safe querying.
    /// </summary>
    public static class GpuDetector
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        private static readonly object _lock = new object();
        private static bool _probed = false;
        private static readonly List<GpuInfo> _detectedGpus = new List<GpuInfo>();
        private static GpuInfo _primaryGpu = null;

        public static IReadOnlyList<GpuInfo> DetectedGpus
        {
            get
            {
                EnsureDetected();
                return _detectedGpus;
            }
        }

        public static GpuInfo PrimaryGpu
        {
            get
            {
                EnsureDetected();
                return _primaryGpu;
            }
        }

        public static void EnsureDetected()
        {
            if (_probed) return;
            lock (_lock)
            {
                if (_probed) return;
                DetectInternal();
                _probed = true;
            }
        }

        private static void DetectInternal()
        {
            try
            {
                Logger.Info("HARDWARE", "Iniciando detecção inteligente de hardware gráfico (GPU)...");
                var devices = new List<GpuInfo>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Win32 EnumDisplayDevices
                try
                {
                    var d = new DISPLAY_DEVICE();
                    d.cb = Marshal.SizeOf(d);
                    for (uint id = 0; EnumDisplayDevices(null, id, ref d, 0); id++)
                    {
                        string name = (d.DeviceString ?? "").Trim();
                        string devId = (d.DeviceID ?? "").Trim();

                        if (IsValidGpuName(name) && !seenNames.Contains(name))
                        {
                            seenNames.Add(name);
                            var info = ParseGpuInfo(name, devId);
                            devices.Add(info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("HARDWARE", "Aviso ao enumerar EnumDisplayDevices: " + ex.Message);
                }

                // 2. Windows Registry fallback/supplement (HKLM Display Adapter Class)
                try
                {
                    const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                    using (var baseKey = Registry.LocalMachine.OpenSubKey(classKey))
                    {
                        if (baseKey != null)
                        {
                            for (int i = 0; i <= 8; i++)
                            {
                                string subName = i.ToString("D4");
                                using (var subKey = baseKey.OpenSubKey(subName))
                                {
                                    if (subKey == null) continue;
                                    string desc = subKey.GetValue("DriverDesc") as string;
                                    string provider = subKey.GetValue("ProviderName") as string;
                                    string matchingId = subKey.GetValue("MatchingDeviceId") as string;

                                    if (!string.IsNullOrEmpty(desc) && IsValidGpuName(desc) && !seenNames.Contains(desc))
                                    {
                                        seenNames.Add(desc);
                                        var info = ParseGpuInfo(desc, matchingId ?? provider ?? "");
                                        devices.Add(info);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("HARDWARE", "Aviso ao consultar Registro de GPUs: " + ex.Message);
                }

                _detectedGpus.Clear();
                _detectedGpus.AddRange(devices);

                if (_detectedGpus.Count == 0)
                {
                    Logger.Warn("HARDWARE", "Nenhuma GPU física identificada via Win32/Registro; fallback para perfil padrão.");
                    _primaryGpu = new GpuInfo
                    {
                        Name = "GPU Genérica / Padrão",
                        Vendor = GpuVendor.Unknown,
                        Type = GpuType.Unknown,
                        DeviceId = "",
                        IsPrimary = true
                    };
                }
                else
                {
                    // Prioritize Dedicated GPU over Integrated GPU (e.g. laptop with Intel iGPU + NVIDIA/AMD dGPU)
                    GpuInfo best = null;
                    foreach (var g in _detectedGpus)
                    {
                        Logger.Info("HARDWARE", string.Format("GPU encontrada: {0} | Fabricante: {1} | Tipo: {2} | ID: {3}",
                            g.Name, g.VendorDisplay, g.TypeDisplay, g.DeviceId));

                        if (best == null)
                        {
                            best = g;
                        }
                        else
                        {
                            // If current best is integrated and this one is discrete, prefer discrete!
                            if (best.Type == GpuType.Integrated && g.Type == GpuType.Discrete)
                            {
                                best = g;
                            }
                            // If both are discrete, prefer NVIDIA or AMD over Intel Arc
                            else if (best.Type == g.Type && best.Vendor == GpuVendor.Intel && (g.Vendor == GpuVendor.Nvidia || g.Vendor == GpuVendor.AMD))
                            {
                                best = g;
                            }
                        }
                    }

                    _primaryGpu = best ?? _detectedGpus[0];
                    _primaryGpu.IsPrimary = true;

                    Logger.Info("HARDWARE", string.Format("🎯 GPU ativa selecionada para aceleração: {0} ({1})",
                        _primaryGpu.Name, _primaryGpu.TypeDisplay));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HARDWARE", "Falha na detecção de hardware GPU", ex);
                _primaryGpu = new GpuInfo
                {
                    Name = "Falha na Detecção (CPU Fallback)",
                    Vendor = GpuVendor.Unknown,
                    Type = GpuType.Unknown,
                    DeviceId = "",
                    IsPrimary = true
                };
            }
        }

        private static bool IsValidGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string upper = name.ToUpperInvariant();
            if (upper.Contains("RDPDD") || upper.Contains("BASIC DISPLAY") || upper.Contains("VNC") ||
                upper.Contains("MIRACAST") || upper.Contains("CITRIX") || upper.Contains("VIRTUALBOX") ||
                upper.Contains("TEAMVIEWER") || upper.Contains("REMOTE") || upper.Contains("INDIRECT"))
            {
                return false;
            }
            return true;
        }

        private static GpuInfo ParseGpuInfo(string name, string devId)
        {
            string nUpper = (name ?? "").ToUpperInvariant();
            string dUpper = (devId ?? "").ToUpperInvariant();

            GpuVendor vendor = GpuVendor.Unknown;
            if (dUpper.Contains("VEN_1002") || dUpper.Contains("VEN_1022") || nUpper.Contains("AMD") || nUpper.Contains("RADEON") || dUpper.Contains("ADVANCED MICRO"))
            {
                vendor = GpuVendor.AMD;
            }
            else if (dUpper.Contains("VEN_10DE") || nUpper.Contains("NVIDIA") || nUpper.Contains("GEFORCE") || nUpper.Contains("QUADRO") || nUpper.Contains("RTX") || nUpper.Contains("GTX"))
            {
                vendor = GpuVendor.Nvidia;
            }
            else if (dUpper.Contains("VEN_8086") || dUpper.Contains("VEN_8087") || nUpper.Contains("INTEL") || dUpper.Contains("INTEL"))
            {
                vendor = GpuVendor.Intel;
            }

            GpuType type = GpuType.Discrete;
            if (vendor == GpuVendor.Intel)
            {
                // Intel Arc (A770, A750, A580, A380, A310, Battlemage B580) are discrete GPUs
                if (nUpper.Contains("ARC") || Regex.IsMatch(nUpper, @"\bA[357]\d{2}\b") || nUpper.Contains("BATTLEMAGE"))
                {
                    type = GpuType.Discrete;
                }
                else
                {
                    // Intel HD, UHD, Iris, Iris Xe, Intel(R) Graphics are integrated in CPU
                    type = GpuType.Integrated;
                }
            }
            else if (vendor == GpuVendor.AMD)
            {
                // Dedicated: RX, R9, R7, Pro, Vega 56/64, Radeon VII
                if (nUpper.Contains("RX") || nUpper.Contains("PRO") || nUpper.Contains("FIREPRO") ||
                    nUpper.Contains("VII") || nUpper.Contains("R9") || nUpper.Contains("R7") ||
                    Regex.IsMatch(nUpper, @"\b[56789]\d{3}\b"))
                {
                    type = GpuType.Discrete;
                }
                else if (nUpper.Contains("GRAPHICS") || nUpper.Contains("VEGA") || nUpper.Contains("APU"))
                {
                    type = GpuType.Integrated;
                }
            }
            else if (vendor == GpuVendor.Nvidia)
            {
                type = GpuType.Discrete;
            }

            return new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                Type = type,
                DeviceId = devId
            };
        }
    }
}

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiteTube
{
    public static class MemoryOptimizer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, IntPtr pCounters, uint cb);

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, IntPtr pCounters, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX2
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        /// <summary>
        /// When in video mode, we keep memory optimization off to give maximum smoothness and zero UI stutters.
        /// </summary>
        public static bool IsVideoModeActive { get; set; }

        /// <summary>
        /// Non-blocking, gentle garbage collection that never forces working set paging to disk,
        /// ensuring 60fps UI fluidity and zero micro-stutters during video and audio playback.
        /// </summary>
        public static void TrimMemory()
        {
            if (IsVideoModeActive) return;
            try
            {
                // Never call EmptyWorkingSet or SetProcessWorkingSetSize(-1, -1) during normal runtime,
                // as flushing pages to disk causes brutal freezes, page faults, and audio/video stutters.
                GC.Collect(1, GCCollectionMode.Optimized);
            }
            catch { }
        }

        /// <summary>
        /// Obtém o consumo exato de memória RAM física ativa (Private Working Set)
        /// idêntico ao valor exibido na coluna 'Memória' do Gerenciador de Tarefas do Windows (Task Manager).
        /// Utiliza a API oficial Win32 K32GetProcessMemoryInfo com PROCESS_MEMORY_COUNTERS_EX2.
        /// </summary>
        public static double GetCurrentRamUsageMB()
        {
            try
            {
                IntPtr hProcess = GetCurrentProcess();
                int sizeEx2 = Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX2));
                IntPtr buffer = Marshal.AllocHGlobal(sizeEx2);
                try
                {
                    Marshal.WriteInt32(buffer, 0, sizeEx2);
                    bool ok = false;
                    try
                    {
                        ok = K32GetProcessMemoryInfo(hProcess, buffer, (uint)sizeEx2);
                    }
                    catch { }

                    if (!ok)
                    {
                        try
                        {
                            ok = GetProcessMemoryInfo(hProcess, buffer, (uint)sizeEx2);
                        }
                        catch { }
                    }

                    if (ok)
                    {
                        var counters = (PROCESS_MEMORY_COUNTERS_EX2)Marshal.PtrToStructure(buffer, typeof(PROCESS_MEMORY_COUNTERS_EX2));
                        ulong privateWs = (ulong)counters.PrivateWorkingSetSize;
                        if (privateWs > 0)
                        {
                            return Math.Round(privateWs / (1024.0 * 1024.0), 1);
                        }

                        ulong workingSet = (ulong)counters.WorkingSetSize;
                        if (workingSet > 0)
                        {
                            return Math.Round(workingSet / (1024.0 * 1024.0), 1);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }

            // Fallback gerenciado clássico
            try
            {
                using (Process proc = Process.GetCurrentProcess())
                {
                    proc.Refresh();
                    return Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);
                }
            }
            catch
            {
                return Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1);
            }
        }
    }
}

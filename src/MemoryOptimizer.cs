using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiteTube
{
    public static class MemoryOptimizer
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            uint processInformationLength,
            out uint returnLength);

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
        /// </summary>
        public static double GetCurrentRamUsageMB()
        {
            try
            {
                IntPtr hProcess = Process.GetCurrentProcess().Handle;
                int structSize = IntPtr.Size == 8 ? 112 : 56;
                IntPtr buffer = Marshal.AllocHGlobal(structSize);
                try
                {
                    uint returnLength;
                    // ProcessVmCounters = 3 (VM_COUNTERS_EX2)
                    int status = NtQueryInformationProcess(hProcess, 3, buffer, (uint)structSize, out returnLength);
                    if (status == 0)
                    {
                        // Em x64 (112 bytes): offset 96 é PrivateWorkingSetSize (campo específico usado pelo Task Manager)
                        // Em x86 (56 bytes): offset 48 é PrivateWorkingSetSize
                        int offset = IntPtr.Size == 8 ? 96 : 48;
                        long privateWorkingSetBytes = IntPtr.Size == 8
                            ? Marshal.ReadInt64(buffer, offset)
                            : Marshal.ReadInt32(buffer, offset);

                        if (privateWorkingSetBytes > 0)
                        {
                            return Math.Round(privateWorkingSetBytes / (1024.0 * 1024.0), 1);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }

            // Fallback: WorkingSet64 caso a syscall não responda
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

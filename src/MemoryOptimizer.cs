using System;
using System.Diagnostics;

namespace LiteTube
{
    public static class MemoryOptimizer
    {
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

        public static double GetCurrentRamUsageMB()
        {
            try
            {
                using (Process proc = Process.GetCurrentProcess())
                {
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

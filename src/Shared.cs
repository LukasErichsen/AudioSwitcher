using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HeadphoneSwitcher
{
    internal static class AppPaths
    {
        public static readonly string AppDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitcher");
        public static readonly string ConfigPath = Path.Combine(AppDirectory, "audio-switcher.config");
        public static readonly string LogPath = Path.Combine(AppDirectory, "audio-switcher.log");
    }
    internal sealed class OperationGate : IDisposable
    {
        private readonly Mutex mutex;
        public bool Acquired { get; private set; }
        public OperationGate(string scope, int waitMilliseconds)
        {
            mutex = new Mutex(false, "Local\\AudioSwitcher." + AtomicFile.Hash(Path.GetFullPath(scope).ToUpperInvariant()));
            try { Acquired = mutex.WaitOne(waitMilliseconds); }
            catch (AbandonedMutexException) { Acquired = true; }
        }
        public void Dispose() { if (Acquired) mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    internal static class TriggerGuard
    {
        public static bool Accept(string path, DateTime now)
        {
            long ticks;
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path), out ticks))
            {
                long elapsed = now.Ticks - ticks;
                if (elapsed >= 0 && elapsed < TimeSpan.FromMilliseconds(550).Ticks) return false;
            }
            AtomicFile.Write(path, now.Ticks.ToString(CultureInfo.InvariantCulture), false);
            return true;
        }
    }
    internal static class Logger
    {
        public static void Write(string path, string message) { Write(path, message, null); }
        public static void Write(string path, string message, Exception ex)
        {
            try
            {
                using (var gate = new OperationGate(path, 1000))
                {
                    if (!gate.Acquired) return;
                    string stamp = path + ".retention";
                    if (!File.Exists(stamp) || File.GetLastWriteTimeUtc(stamp).Date < DateTime.UtcNow.Date)
                    {
                        try
                        {
                            if (File.Exists(path)) AtomicFile.Write(path, Retain(File.ReadAllText(path), DateTime.Now.AddDays(-365)), false);
                            AtomicFile.Write(stamp, DateTime.UtcNow.ToString("O"), false);
                        }
                        catch { /* If cleanup is blocked, still try to append this diagnostic entry. */ }
                    }
                    string entry = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + message.Replace('\r', ' ').Replace('\n', ' ');
                    if (ex != null) entry += Environment.NewLine + ex;
                    File.AppendAllText(path, entry + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { /* Diagnostics must never crash the toggle. */ }
        }
        public static string Retain(string text, DateTime cutoff)
        {
            var kept = new StringBuilder();
            bool keep = true;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int end = line.IndexOf(']');
                    DateTime date;
                    if (line.StartsWith("[") && end > 0)
                    {
                        string[] formats = { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH.mm.ss" };
                        keep = !DateTime.TryParseExact(line.Substring(1, end - 1), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date >= cutoff;
                    }
                    if (keep) kept.AppendLine(line);
                }
            }
            return kept.ToString();
        }
    }
}

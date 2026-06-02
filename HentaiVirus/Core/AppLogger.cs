using System;
using System.Diagnostics;
using System.IO;

namespace HentaiVirus.Core
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();

        public static void Log(Exception exception, string? context = null)
        {
            string prefix = string.IsNullOrWhiteSpace(context) ? "Exception" : context;
            WriteLine($"{DateTimeOffset.Now:O} [{prefix}] {exception.Message}");
        }

        public static void Log(string message)
        {
            WriteLine($"{DateTimeOffset.Now:O} [Info] {message}");
        }

        private static void WriteLine(string line)
        {
            try
            {
                lock (SyncRoot)
                {
                    File.AppendAllText(AppPaths.LogPath, line + Environment.NewLine);
                }
            }
            catch (Exception loggingException)
            {
                Debug.WriteLine($"Failed to write application log: {loggingException.Message}");
            }
        }
    }
}

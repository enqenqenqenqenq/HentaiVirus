using System;
using System.Diagnostics;
using System.IO;

namespace SilentPackageManager.Core
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();
        public static event Action<string>? OnLogMessage;

        public static void Log(Exception exception, string? context = null)
        {
            string prefix = string.IsNullOrWhiteSpace(context) ? "Exception" : context;
            WriteLine($"[{prefix}] {exception.Message}");
        }

        public static void Log(string message)
        {
            WriteLine($"[Info] {message}");
        }

        private static void WriteLine(string line)
        {
            string formattedLine = $"{DateTimeOffset.Now:HH:mm:ss} {line}";
        
            OnLogMessage?.Invoke(formattedLine);

            try
            {
                lock (SyncRoot)
                {
                    File.AppendAllText(AppPaths.LogPath, formattedLine + Environment.NewLine);
                }
            }
            catch (Exception loggingException)
            {
                Debug.WriteLine($"Failed to write application log: {loggingException.Message}");
            }
        }
    }
}

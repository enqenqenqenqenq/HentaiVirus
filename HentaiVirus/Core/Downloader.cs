using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace HentaiVirus.Core
{
    public class Downloader
    {
        private static readonly HttpClientHandler _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };

        private static readonly HttpClient _httpClient = new HttpClient(_handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
        };

        public async Task<string> DownloadAndExtractAsync(string url, string targetFolder, CancellationToken cancellationToken)
        {
            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, true);
            }
            Directory.CreateDirectory(targetFolder);

            string packagePath = Path.Combine(targetFolder, "package.download");
            bool packageWasMoved = false;

            try
            {
                await DownloadFileAsync(url, packagePath, cancellationToken).ConfigureAwait(false);

                if (IsWindowsExecutable(packagePath))
                {
                    string exePath = Path.Combine(targetFolder, "downloaded.exe");
                    File.Move(packagePath, exePath, overwrite: true);
                    packageWasMoved = true;
                    return exePath;
                }

                EnsureDownloadedFileCanBeArchive(packagePath, url);
                ExtractArchiveSafely(packagePath, targetFolder);
                return FindMainExecutable(targetFolder);
            }
            finally
            {
                if (!packageWasMoved)
                {
                    DeleteTemporaryFile(packagePath);
                }
            }
        }

        private static async Task DownloadFileAsync(string url, string packagePath, CancellationToken cancellationToken)
        {
            var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                string htmlContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                
                var match = System.Text.RegularExpressions.Regex.Match(htmlContent, @"confirm=([a-zA-Z0-9_-]+)");
                
                if (match.Success)
                {
                    string confirmToken = match.Groups[1].Value;
                    
                    string confirmedUrl = url;
                    if (url.Contains("confirm=t"))
                    {
                        confirmedUrl = url.Replace("confirm=t", $"confirm={confirmToken}");
                    }
                    else
                    {
                        confirmedUrl += $"&confirm={confirmToken}";
                    }

                    response.Dispose();
                    response = await _httpClient
                        .GetAsync(confirmedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            response.EnsureSuccessStatusCode();

            await using Stream httpStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var fileStream = new FileStream(
                packagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            long? totalBytes = response.Content.Headers.ContentLength;
            long totalRead = 0;
            byte[] buffer = new byte[81920];
            int bytesRead;
            DateTime lastLogTime = DateTime.Now;

            AppLogger.Log($"Начало скачивания. Размер файла: {(totalBytes.HasValue ? (totalBytes.Value / 1048576).ToString() + " МБ" : "неизвестен")}");

            while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalRead += bytesRead;

                if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                {
                    long readMb = totalRead / 1048576;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        long totalMb = totalBytes.Value / 1048576;
                        long percent = (totalRead * 100) / totalBytes.Value;
                        AppLogger.Log($"Загрузка: {readMb} МБ из {totalMb} МБ ({percent}%)");
                    }
                    else
                    {
                        AppLogger.Log($"Загружено: {readMb} МБ");
                    }
                    lastLogTime = DateTime.Now;
                }
            }

            AppLogger.Log("Загрузка архива завершена. Начинается разархивирование...");
        }

        private static void EnsureDownloadedFileCanBeArchive(string packagePath, string url)
        {
            var fileInfo = new FileInfo(packagePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                throw new InvalidDataException($"Downloaded file is empty. Url: {url}");
            }

            byte[] header = ReadHeader(packagePath, 512);
            if (LooksLikeHtml(header))
            {
                throw new InvalidDataException($"Downloaded HTML page instead of archive/exe. Use a direct download link. Url: {url}");
            }
        }

        private static void ExtractArchiveSafely(string packagePath, string targetFolder)
        {
            string destinationRoot = Path.GetFullPath(targetFolder)
                                         .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                     + Path.DirectorySeparatorChar;

            using Stream stream = File.OpenRead(packagePath);

            IReader? reader = null;
    
            try
            {
                reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
            }
            catch (Exception)
            {
                // Игнорируем ошибки инициализации. Reader останется null.
            }

            if (reader != null)
            {
                using (reader)
                {
                    ExtractWithReader(reader, destinationRoot);
                }
            }
            else
            {
                AppLogger.Log("Формат требует нативной распаковки. Запуск 7z.dll...");
                // Важно: закрываем текущий файловый поток перед передачей файла нативной библиотеке
                stream.Close(); 
        
                ExtractWithNative7z(packagePath, destinationRoot);
            }
        }

        private static void ExtractWithNative7z(string packagePath, string destinationRoot)
        {
            string libraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z.dll");
    
            if (!File.Exists(libraryPath))
            {
                throw new FileNotFoundException("Файл 7z.dll не найден по пути: " + libraryPath);
            }

            // Инициализируем нативную библиотеку
            SevenZip.SevenZipBase.SetLibraryPath(libraryPath);

            using var extractor = new SevenZip.SevenZipExtractor(packagePath);
    
            DateTime lastLogTime = DateTime.Now;
    
            // Подписка на событие для вывода логов
            extractor.Extracting += (sender, args) =>
            {
                if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                {
                    AppLogger.Log($"Распаковка [Native 7z]: завершено {args.PercentDone}%...");
                    lastLogTime = DateTime.Now;
                }
            };

            // Запуск многопоточной нативной распаковки
            extractor.ExtractArchive(destinationRoot);
    
            AppLogger.Log("Распаковка [Native 7z] успешно завершена.");
        }

        private static void ExtractWithReader(IReader reader, string destinationRoot)
        {
            long extractedSize = 0;
            int fileCount = 0;
            DateTime lastLogTime = DateTime.Now;

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;

                // Явное извлечение и проверка ключа для устранения CS8604
                string entryKey = reader.Entry.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(entryKey)) continue;

                string destinationPath = ValidateAndCreateDirectory(entryKey, destinationRoot);

                reader.WriteEntryToFile(destinationPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });

                extractedSize += reader.Entry.Size;
                fileCount++;

                LogProgress(ref lastLogTime, fileCount, extractedSize, "Reader API");
            }
        }

        private static void ExtractWithArchive(IArchive archive, string destinationRoot)
        {
            long extractedSize = 0;
            int fileCount = 0;
            DateTime lastLogTime = DateTime.Now;

            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                // Явное извлечение и проверка ключа для устранения CS8604
                string entryKey = entry.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(entryKey)) continue;

                string destinationPath = ValidateAndCreateDirectory(entryKey, destinationRoot);

                entry.WriteToFile(destinationPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });

                extractedSize += entry.Size;
                fileCount++;

                LogProgress(ref lastLogTime, fileCount, extractedSize, "Archive API");
            }
        }

        private static string ValidateAndCreateDirectory(string entryKey, string destinationRoot)
        {
            string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryKey));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry escapes target directory: {entryKey}");
            }

            string? parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            return destinationPath;
        }

        private static void LogProgress(ref DateTime lastLogTime, int fileCount, long extractedSize, string apiName)
        {
            if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
            {
                long extractedMb = extractedSize / 1048576;
                AppLogger.Log($"Распаковка [{apiName}]: извлечено {fileCount} файлов ({extractedMb} МБ)...");
                lastLogTime = DateTime.Now;
            }
        }

        private static string FindMainExecutable(string targetFolder)
        {
            var candidate = Directory
                .EnumerateFiles(targetFolder, "*.exe", SearchOption.AllDirectories)
                .Where(path => !IsHelperExecutable(path))
                .Select(path => new FileInfo(path))
                .OrderBy(file => GetPathDepth(targetFolder, file.FullName))
                .ThenByDescending(file => file.Length)
                .FirstOrDefault();

            return candidate?.FullName ?? string.Empty;
        }

        private static int GetPathDepth(string rootFolder, string filePath)
        {
            string relativePath = Path.GetRelativePath(rootFolder, filePath);
            return relativePath.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
        }

        private static bool IsHelperExecutable(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

            return name.Contains("unins", StringComparison.Ordinal)
                   || name.Contains("uninstall", StringComparison.Ordinal)
                   || name.Contains("setup", StringComparison.Ordinal)
                   || name.Contains("install", StringComparison.Ordinal)
                   || name.Contains("redist", StringComparison.Ordinal)
                   || name.Contains("vcredist", StringComparison.Ordinal)
                   || name.Contains("dxsetup", StringComparison.Ordinal)
                   || name.Contains("crashhandler", StringComparison.Ordinal);
        }

        private static bool IsWindowsExecutable(string filePath)
        {
            Span<byte> header = stackalloc byte[2];

            using var stream = File.OpenRead(filePath);
            int readBytes = stream.Read(header);

            return readBytes == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }

        private static byte[] ReadHeader(string filePath, int maxBytes)
        {
            byte[] buffer = new byte[maxBytes];

            using var stream = File.OpenRead(filePath);
            int readBytes = stream.Read(buffer, 0, buffer.Length);

            if (readBytes == buffer.Length)
            {
                return buffer;
            }

            byte[] result = new byte[readBytes];
            Array.Copy(buffer, result, readBytes);
            return result;
        }

        private static bool LooksLikeHtml(byte[] header)
        {
            string text = Encoding.UTF8.GetString(header).TrimStart();

            return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
                   || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("<body", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("<script", StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteTemporaryFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, $"Failed to delete temporary file {filePath}");
            }
        }
    }
}
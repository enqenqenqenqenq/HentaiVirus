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
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(3),
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
        };

        public async Task<string> DownloadAndExtractAsync(string url, string targetFolder, CancellationToken cancellationToken)
        {
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
                
                var match = System.Text.RegularExpressions.Regex.Match(htmlContent, @"confirm=([a-zA-Z0-9_]+)");
                
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

            await httpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
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

            using var archive = ArchiveFactory.OpenArchive(packagePath, new ReaderOptions());
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                string entryName = entry.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    continue;
                }

                string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryName));
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Archive entry escapes target directory: {entryName}");
                }

                string? parentDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                entry.WriteToFile(destinationPath, new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                });
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

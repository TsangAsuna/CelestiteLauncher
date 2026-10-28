using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Cysharp.Threading.Tasks;
using Celestite.Configs;
using Celestite.I18N;
using Celestite.Network.Models;
using Celestite.Utils;
using ZeroLog;

namespace Celestite.Utils
{
    /// <summary>
    /// BepInEx 插件框架自动安装器
    /// 从 GitHub Releases 下载适配游戏的 BepInEx 版本并解压到游戏目录
    /// </summary>
    public static class BepInExInstaller
    {
        private static readonly Log Logger = LogManager.GetLogger("BepInExInstaller");
        private static readonly HttpClient HttpClient = new()
        {
            DefaultRequestHeaders =
            {
                { "User-Agent", "CelestiteLauncher/1.0" },
                { "Accept", "application/vnd.github+json" }
            }
        };

        // BepInEx GitHub 仓库
        private const string BepInExOwner = "BepInEx";
        private const string BepInExRepo = "BepInEx";

        /// <summary>
        /// 从 GitHub 获取 BepInEx 最新 release 信息
        /// </summary>
        public static async UniTask<BepInExReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"https://api.github.com/repos/{BepInExOwner}/{BepInExRepo}/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"BepInEx API returned {response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);

                var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
                var assets = doc.RootElement.GetProperty("assets").EnumerateArray();

                var assetInfos = assets
                    .Where(a => a.TryGetProperty("name", out var nameEl) && a.TryGetProperty("browser_download_url", out var urlEl))
                    .Select(a => new BepInExAssetInfo
                    {
                        Name = nameEl.GetString() ?? string.Empty,
                        DownloadUrl = urlEl.GetString() ?? string.Empty,
                        Size = a.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0
                    })
                    .ToList();

                return new BepInExReleaseInfo(tagName, assetInfos);
            }
            catch (Exception ex)
            {
                Logger.Error($"GetLatestReleaseAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 为指定游戏安装 BepInEx。自动选择 x64 版本（绝大多数 DMM 客户端游戏为 64 位）。
        /// </summary>
        public static async UniTask<bool> InstallAsync(string gameInstallDir, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(gameInstallDir) || !Directory.Exists(gameInstallDir))
            {
                Logger.Warn($"Install directory does not exist: {gameInstallDir}");
                return false;
            }

            var release = await GetLatestReleaseAsync(cancellationToken);
            if (release == null || release.Assets.Count == 0)
                return false;

            // 优先选择 x64 版本 zip，其次通用 zip
            var preferred = release.Assets.FirstOrDefault(a =>
                a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (preferred == null)
            {
                Logger.Warn("No suitable BepInEx zip asset found");
                return false;
            }

            Logger.Info($"Installing BepInEx {release.TagName} from {preferred.Name}");

            var zipPath = Path.Combine(Path.GetTempPath(), $"BepInEx_{Guid.NewGuid():N}.zip");
            try
            {
                // 下载 zip
                using var request = new HttpRequestMessage(HttpMethod.Get, preferred.DownloadUrl);
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = File.Create(zipPath);
                await zipStream.CopyToAsync(fileStream, cancellationToken);

                // 解压到游戏目录
                ZipFile.ExtractToDirectory(zipPath, gameInstallDir, overwriteFiles: true);

                Logger.Info($"BepInEx installed to {gameInstallDir}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"InstallAsync failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }
    }

    public record BepInExReleaseInfo(string TagName, List<BepInExAssetInfo> Assets);

    public record BepInExAssetInfo(string Name, string DownloadUrl, long Size);
}

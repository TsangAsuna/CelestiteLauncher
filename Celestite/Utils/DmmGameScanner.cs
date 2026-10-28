using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Celestite.Configs;
using Celestite.Network;
using Celestite.Network.Models;
using Celestite.Utils;

namespace Celestite.Utils
{
    /// <summary>
    /// DMM 游戏自动识别扫描器
    /// 支持多源扫描：
    /// 1. dmmgame.cnf（DMMGamePlayer 本地安装记录，最可靠，含 ProductId + Path）
    /// 2. DMMGamePlayer 安装目录（如 C:\Program Files\DMMGamePlayer）
    /// 3. FANZAGAMES 本地数据目录（如 %LOCALAPPDATA%\..\LocalLow\FANZAGAMES）
    /// </summary>
    public static class DmmGameScanner
    {
        private static readonly ZeroLog.Log Logger = ZeroLog.LogManager.GetLogger("DmmGameScanner");

        // 常见 DMMGamePlayer / FANZA 游戏目录位置
        private static readonly string[] DefaultScanRoots =
        {
            // DMMGamePlayer 程序安装目录
            @"C:\Program Files\DMMGamePlayer",
            @"C:\Program Files (x86)\DMMGamePlayer",
            // FANZAGAMES / DMM GAMES 本地数据目录
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "FANZAGAMES"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "DMMGAMES"),
        };

        /// <summary>
        /// 扫描 DMMGamePlayer 本地已安装但尚未被 Celestite 追踪的游戏
        /// </summary>
        public static async UniTask<List<DetectedDmmGame>> ScanUntrackedInstalledGamesAsync(IEnumerable<MyGame> trackedGames)
        {
            var result = new List<DetectedDmmGame>();
            var trackedSet = new HashSet<(string ProductId, TApiGameType Type)>();
            foreach (var g in trackedGames)
                trackedSet.Add((g.ProductId, g.Type));

            // 源 1：dmmgame.cnf —— 最可靠，直接有 ProductId + InstallPath
            try
            {
                var dmmContents = ConfigUtils.GetAllInstalledContent().ToList();
                foreach (var content in dmmContents)
                {
                    if (trackedSet.Contains((content.ProductId, content.GameType)))
                        continue;
                    if (!content.Detail.Installed || string.IsNullOrEmpty(content.Detail.Path))
                        continue;

                    result.Add(new DetectedDmmGame
                    {
                        ProductId = content.ProductId,
                        GameType = content.GameType,
                        InstallPath = content.Detail.Path,
                        Version = content.Detail.Version,
                        Title = string.Empty,
                        Source = nameof(DetectedGameSource.DmmConfig)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[DmmGameScanner] dmmgame.cnf scan failed: {ex.Message}");
            }

            // 源 2 & 3：文件系统扫描 DMMGamePlayer 安装目录 / FANZAGAMES 目录
            // 用于发现 dmmgame.cnf 中未记录但实际已安装的游戏
            foreach (var root in DefaultScanRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    await ScanDirectoryTreeAsync(root, trackedSet, result);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[DmmGameScanner] directory scan failed for {root}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 递归扫描目录树，寻找可能的 DMM 游戏安装文件夹
        /// 判断依据：文件夹名包含 DMM 游戏 ID 特征，或包含常见游戏启动器文件
        /// </summary>
        private static async UniTask ScanDirectoryTreeAsync(string root, HashSet<(string, TApiGameType)> trackedSet, List<DetectedDmmGame> result, int depth = 0)
        {
            // 最多扫描 3 层，避免过深遍历
            if (depth > 3) return;

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(root);
            }
            catch
            {
                return;
            }

            foreach (var dir in dirs)
            {
                var dirName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(dirName)) continue;

                // 跳过常见非游戏目录
                if (IsSkipDirectory(dirName)) continue;

                // 检查是否看起来像 DMM 游戏安装目录
                bool looksLikeGame = IsLikelyDmmGameDir(dir, dirName);
                if (!looksLikeGame)
                {
                    // 不是游戏目录，递归进入子目录继续扫描
                    await ScanDirectoryTreeAsync(dir, trackedSet, result, depth + 1);
                    continue;
                }

                // 尝试从目录名推断 ProductId
                var candidateId = ExtractPossibleProductId(dirName);
                if (string.IsNullOrEmpty(candidateId))
                {
                    // 目录名不可用，尝试递归子目录
                    await ScanDirectoryTreeAsync(dir, trackedSet, result, depth + 1);
                    continue;
                }

                // 避免重复
                if (result.Any(x => x.ProductId.Equals(candidateId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // 尝试从游戏目录中读取更准确的 ProductId（如 product_id.txt）
                var productIdFile = Path.Combine(dir, "product_id.txt");
                if (File.Exists(productIdFile))
                {
                    try
                    {
                        var fileId = File.ReadAllText(productIdFile).Trim();
                        if (!string.IsNullOrEmpty(fileId))
                            candidateId = fileId;
                    }
                    catch { }
                }

                result.Add(new DetectedDmmGame
                {
                    ProductId = candidateId,
                    GameType = TApiGameType.GCL, // 默认，后续可通过 API 补全
                    InstallPath = dir,
                    Version = string.Empty,
                    Title = string.Empty,
                    Source = nameof(DetectedGameSource.FileSystem)
                });
            }
        }

        private static bool IsSkipDirectory(string dirName)
        {
            var lower = dirName.ToLowerInvariant();
            return lower == "node_modules" ||
                   lower == ".git" ||
                   lower == "__pycache__" ||
                   lower.StartsWith("$") ||
                   lower == "temp" ||
                   lower == "tmp" ||
                   lower == "cache" ||
                   lower == "logs" ||
                   lower == "log";
        }

        /// <summary>
        /// 粗略判断目录是否像 DMM 游戏安装目录：
        /// 存在 .exe 启动文件，或包含 DMM 特有的子目录结构
        /// </summary>
        private static bool IsLikelyDmmGameDir(string dirPath, string dirName)
        {
            try
            {
                // 有 .exe 文件
                var exes = Directory.GetFiles(dirPath, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length > 0) return true;

                // 有常见游戏数据子目录
                var subDirs = Directory.GetDirectories(dirPath);
                foreach (var sd in subDirs)
                {
                    var name = Path.GetFileName(sd)?.ToLowerInvariant() ?? string.Empty;
                    if (name == "game" || name == "data" || name == "bin" || name == "dmmgame" || name == "contents")
                        return true;
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        /// <summary>
        /// 从目录名提取可能的 DMM ProductId。
        /// DMM 游戏 ID 通常形如 app_123456，或纯英文 ID。
        /// 这里保守处理：只要目录名不是明显的系统目录，就作为候选。
        /// </summary>
        private static string? ExtractPossibleProductId(string dirName)
        {
            if (string.IsNullOrWhiteSpace(dirName)) return null;
            var trimmed = dirName.Trim();
            // 过滤掉明显的版本号/语言目录
            if (trimmed.Length < 3) return null;
            if (trimmed.All(char.IsDigit)) return null;
            return trimmed;
        }

        /// <summary>
        /// 将检测到的游戏添加到 Celestite 追踪列表（通过 DMM API 加入我的游戏）
        /// </summary>
        public static async UniTask<bool> AddDetectedGameToCelestiteAsync(string productId, TApiGameType gameType)
        {
            var advancedType = DmmTypeApiGameHelper.GetGameTypeDataFromApiGameType(gameType);
            var gameTypeResult = await DmmGamePlayerApiHelper.GetGameTypeFromOldKeys(productId, REQUEST_FLOOR.FREE, adult: advancedType.IsAdult);
            if (gameTypeResult.Failed) return false;

            var addResult = await DmmGamePlayerApiHelper.AddStoreProductToMyGame(gameTypeResult.Value);
            if (addResult.Failed) return false;

            // 尝试从 dmmgame.cnf 读取现有路径，标记为已安装
            if (ConfigUtils.TryGetContent(productId, gameType, out var content))
            {
                ConfigUtils.UpdateLocalContent(productId, gameType, new DmmGameCnfContentDetail
                {
                    Installed = true,
                    KeyBindSettingVer = string.Empty,
                    Path = content.Detail.Path,
                    Version = content.Detail.Version,
                    Shortcut = string.Empty
                });
            }

            return true;
        }

        /// <summary>
        /// 获取建议的默认下载目录（优先用户已配置，其次 F:\DMMDownload，再次 <exe>/Games）
        /// </summary>
        public static string GetSuggestedDownloadDirectory()
        {
            var configured = ConfigUtils.DmmGamesDefaultInstallFolder;
            if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
                return configured;

            // 用户实际使用的下载目录
            var fDrive = @"F:\DMMDownload";
            if (Directory.Exists(fDrive) || DriveInfo.GetDrives().Any(d => d.Name == @"F:\"))
                return fDrive;

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games");
        }
    }

    public enum DetectedGameSource
    {
        DmmConfig,
        FileSystem
    }

    public class DetectedDmmGame
    {
        public string ProductId { get; set; } = string.Empty;
        public TApiGameType GameType { get; set; }
        public string InstallPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        public string DisplayName => string.IsNullOrEmpty(Title) ? ProductId : Title;

        public string SourceLabel => Source switch
        {
            nameof(DetectedGameSource.DmmConfig) => "DMMGamePlayer 配置",
            nameof(DetectedGameSource.FileSystem) => "文件系统扫描",
            _ => "未知"
        };
    }
}

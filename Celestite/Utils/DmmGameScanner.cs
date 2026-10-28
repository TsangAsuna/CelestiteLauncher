using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cysharp.Threading.Tasks;
using Celestite.Configs;
using Celestite.Network;
using Celestite.Network.Models;
using Celestite.Utils;

namespace Celestite.Utils
{
    /// <summary>
    /// DMM 游戏自动识别扫描器
    /// 读取 DMMGamePlayer 本地配置 (dmmgame.cnf)，找出已安装但尚未被 Celestite 追踪的游戏
    /// </summary>
    public static class DmmGameScanner
    {
        /// <summary>
        /// 扫描 DMMGamePlayer 本地已安装游戏，返回尚未被 Celestite 游戏列表追踪的条目
        /// </summary>
        public static async UniTask<List<DetectedDmmGame>> ScanUntrackedInstalledGamesAsync(IEnumerable<MyGame> trackedGames)
        {
            var result = new List<DetectedDmmGame>();

            // GetAllInstalledContent() internally reads dmmgame.cnf
            var dmmContents = ConfigUtils.GetAllInstalledContent().ToList();

            // 已被 Celestite 追踪的游戏集合 (ProductId + GameType)
            var trackedSet = new HashSet<(string ProductId, TApiGameType Type)>();
            foreach (var g in trackedGames)
                trackedSet.Add((g.ProductId, g.Type));

            foreach (var content in dmmContents)
            {
                if (trackedSet.Contains((content.ProductId, content.GameType)))
                    continue;

                // 只处理已安装且路径有效的游戏
                if (!content.Detail.Installed || string.IsNullOrEmpty(content.Detail.Path))
                    continue;

                result.Add(new DetectedDmmGame
                {
                    ProductId = content.ProductId,
                    GameType = content.GameType,
                    InstallPath = content.Detail.Path,
                    Version = content.Detail.Version,
                    Title = string.Empty // 标题需要从 API 获取，先留空
                });
            }

            return result;
        }

        /// <summary>
        /// 将检测到的游戏添加到 Celestite 追踪列表（通过 DMM API 加入我的游戏）
        /// </summary>
        public static async UniTask<bool> AddDetectedGameToCelestiteAsync(string productId, TApiGameType gameType)
        {
            // 先通过 API 将游戏加入用户的“我的游戏”
            var advancedType = DmmTypeApiGameHelper.GetGameTypeDataFromApiGameType(gameType);
            var gameTypeResult = await DmmGamePlayerApiHelper.GetGameTypeFromOldKeys(productId, REQUEST_FLOOR.FREE, adult: advancedType.IsAdult);
            if (gameTypeResult.Failed) return false;

            var addResult = await DmmGamePlayerApiHelper.AddStoreProductToMyGame(gameTypeResult.Value);
            if (addResult.Failed) return false;

            // 标记为已安装，路径从 dmmgame.cnf 读取
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
    }

    public class DetectedDmmGame
    {
        public string ProductId { get; set; } = string.Empty;
        public TApiGameType GameType { get; set; }
        public string InstallPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;

        public string DisplayName => string.IsNullOrEmpty(Title) ? ProductId : Title;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using Celestite.Configs;
using Celestite.I18N;
using Celestite.Network;
using Celestite.Network.Models;
using Celestite.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;

namespace Celestite.ViewModels.Dialogs
{
    public class DetectedGameItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private bool _isSelected;
        public DetectedDmmGame Game { get; }
        public string Title => string.IsNullOrEmpty(Game.Title) ? Game.ProductId : Game.Title;
        public string ProductId => Game.ProductId;
        public string GameType => Game.GameType.ToString();
        public string InstallPath => Game.InstallPath;
        public string Version => Game.Version;
        public string SourceLabel => Game.SourceLabel;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public DetectedGameItem(DetectedDmmGame game)
        {
            Game = game;
        }
    }

    public partial class AutoDetectDmmGamesDialogViewModel : ViewModelBase
    {
        [ObservableProperty] private ObservableCollection<DetectedGameItem> _detectedGames = [];
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public async UniTask LoadDetectedGamesAsync(IEnumerable<MyGame> trackedGames)
        {
            _isBusy = true;
            StatusMessage = Localization.AutoDetectScanning;

            try
            {
                var detected = await DmmGameScanner.ScanUntrackedInstalledGamesAsync(trackedGames);
                DetectedGames.Clear();

                // 并发获取每个检测到游戏的标题（通过 DMM API 的搜索或游戏类型接口）
                var tasks = detected.Select(async d =>
                {
                    await TryFillTitleAsync(d);
                    return new DetectedGameItem(d);
                });
                var items = await UniTask.WhenAll(tasks);

                foreach (var item in items)
                    DetectedGames.Add(item);

                StatusMessage = DetectedGames.Count == 0
                    ? Localization.AutoDetectNoNewGames
                    : ZString.Format(Localization.AutoDetectFoundCount, DetectedGames.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = ZString.Format(Localization.AutoDetectError, ex.Message);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private static async UniTask TryFillTitleAsync(DetectedDmmGame game)
        {
            try
            {
                // 尝试通过 DMM API 获取游戏类型信息，顺带确认游戏存在
                var advancedType = DmmTypeApiGameHelper.GetGameTypeDataFromApiGameType(game.GameType);
                var result = await DmmGamePlayerApiHelper.GetGameTypeFromOldKeys(game.ProductId, REQUEST_FLOOR.FREE, adult: advancedType.IsAdult);
                if (result.Success)
                {
                    // 这里 API 只返回类型信息，不返回标题。标题保持为 ProductId，
                    // 用户可以在添加后通过游戏设置查看完整信息。
                    game.GameType = result.Value.GameType;
                }
            }
            catch
            {
                // 标题获取失败不影响流程，保持 ProductId 作为显示名
            }
        }

        [RelayCommand]
        private async Task AddSelectedGames()
        {
            var selected = DetectedGames.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                StatusMessage = Localization.AutoDetectSelectAtLeastOne;
                return;
            }

            _isBusy = true;
            var successCount = 0;

            foreach (var item in selected)
            {
                StatusMessage = ZString.Format(Localization.AutoDetectAdding, item.Title);
                var ok = await DmmGameScanner.AddDetectedGameToCelestiteAsync(item.ProductId, item.GameType);
                if (ok) successCount++;
            }

            // 触发游戏列表刷新
            DmmGamePlayerApiHelper.ForceRaiseReGetGamesRequiredEvent();

            StatusMessage = ZString.Format(Localization.AutoDetectAddComplete, successCount, selected.Count);
            _isBusy = false;
        }

        [RelayCommand]
        private void Close() { }
    }
}

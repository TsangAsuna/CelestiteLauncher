using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        public string Title => Game.DisplayName;
        public string ProductId => Game.ProductId;
        public string GameType => Game.GameType.ToString();
        public string InstallPath => Game.InstallPath;
        public string Version => Game.Version;

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

        public AutoDetectDmmGamesDialogViewModel()
        {
        }

        public async UniTask LoadDetectedGamesAsync(IEnumerable<MyGame> trackedGames)
        {
            _isBusy = true;
            StatusMessage = Localization.AutoDetectScanning;

            try
            {
                var detected = await DmmGameScanner.ScanUntrackedInstalledGamesAsync(trackedGames);
                DetectedGames.Clear();
                foreach (var d in detected)
                    DetectedGames.Add(new DetectedGameItem(d));

                StatusMessage = DetectedGames.Count == 0
                    ? Localization.AutoDetectNoNewGames
                    : Cysharp.Text.ZString.Format(Localization.AutoDetectFoundCount, DetectedGames.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Cysharp.Text.ZString.Format(Localization.AutoDetectError, ex.Message);
            }
            finally
            {
                _isBusy = false;
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
                StatusMessage = Cysharp.Text.ZString.Format(Localization.AutoDetectAdding, item.Title);
                var ok = await DmmGameScanner.AddDetectedGameToCelestiteAsync(item.ProductId, item.GameType);
                if (ok) successCount++;
            }

            StatusMessage = Cysharp.Text.ZString.Format(Localization.AutoDetectAddComplete, successCount, selected.Count);
            _isBusy = false;
        }

        [RelayCommand]
        private void Close() { }
    }
}

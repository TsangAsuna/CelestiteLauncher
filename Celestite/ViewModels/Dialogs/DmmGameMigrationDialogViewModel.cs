using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Celestite.Configs;
using Celestite.I18N;
using Celestite.Network.Models;
using Celestite.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using FluentAvalonia.UI.Controls;
using ZeroLog;

namespace Celestite.ViewModels.Dialogs
{
    public partial class DmmGameMigrationDialogViewModel : ViewModelBase
    {
        private static readonly Log Logger = LogManager.GetLogger("GameMigration");

        [ObservableProperty] private MyGame _gameData;
        [ObservableProperty] private string _currentGamePath = string.Empty;
        [ObservableProperty] private string _targetGamePath = string.Empty;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _statusMessage = string.Empty;
        [ObservableProperty] private double _progress;
        [ObservableProperty] private bool _deleteSourceAfterMigration;
        [ObservableProperty] private bool _isCancellationRequested;

        private DmmGameCnfContent? _content;
        private CancellationTokenSource? _migrationCts;

        public DmmGameMigrationDialogViewModel(MyGame gameData)
        {
            GameData = gameData;
            _content = null;
            if (ConfigUtils.TryGetContent(gameData.ProductId, gameData.Type, out var c))
                _content = c;

            CurrentGamePath = _content?.Detail.Path ?? string.Empty;

            // 默认目标路径：优先 F:\DMMDownload（用户自定义下载目录），其次当前路径的父目录
            var fDriveDownload = @"F:\DMMDownload";
            var folderName = Path.GetFileName(CurrentGamePath);
            if (Directory.Exists(fDriveDownload))
            {
                TargetGamePath = Path.Combine(fDriveDownload, folderName ?? string.Empty);
            }
            else if (Directory.Exists(CurrentGamePath))
            {
                var parent = Directory.GetParent(CurrentGamePath);
                if (parent != null)
                    TargetGamePath = Path.Combine(parent.FullName, $"{folderName}_migrated");
            }
        }

        [RelayCommand]
        private async Task PickTargetFolder()
        {
            var picker =
                await ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!
                    .StorageProvider.OpenFolderPickerAsync(
                        new FolderPickerOpenOptions()
                        {
                            Title = Localization.MigrateSelectTargetFolderTitle,
                            AllowMultiple = false
                        }
                    );
            if (picker.Count == 1)
                TargetGamePath = picker[0].Path.LocalPath;
        }

        [RelayCommand]
        private void Close() { }

        [RelayCommand]
        private async Task Migrate()
        {
            if (_isBusy) return;
            if (string.IsNullOrEmpty(CurrentGamePath) || !Directory.Exists(CurrentGamePath))
            {
                StatusMessage = Localization.MigrateSourcePathInvalid;
                return;
            }
            if (string.IsNullOrEmpty(TargetGamePath))
            {
                StatusMessage = Localization.MigrateTargetPathRequired;
                return;
            }

            var currentFull = Path.GetFullPath(CurrentGamePath);
            var targetFull = Path.GetFullPath(TargetGamePath);
            if (currentFull.Equals(targetFull, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = Localization.MigrateSamePathError;
                return;
            }

            // 创建取消令牌源
            _migrationCts = new CancellationTokenSource();
            _isCancellationRequested = false;

            _isBusy = true;
            Progress = 0;
            StatusMessage = Localization.MigrateCopyingFiles;

            try
            {
                await CopyDirectoryAsync(currentFull, targetFull, _migrationCts.Token);

                if (_isCancellationRequested)
                {
                    StatusMessage = Localization.MigrateCancelled;
                    return;
                }

                // 更新 dmmgame.cnf 中的路径
                if (_content != null)
                {
                    ConfigUtils.UpdateLocalContent(GameData.ProductId, GameData.Type, new DmmGameCnfContentDetail
                    {
                        Installed = true,
                        KeyBindSettingVer = string.Empty,
                        Path = targetFull,
                        Version = _content.Detail.Version,
                        Shortcut = _content.Detail.Shortcut
                    });
                    StatusMessage = Localization.MigrateSuccess;
                    Logger.Info($"Game migration completed: {GameData.ProductId} -> {targetFull}");
                }

                if (_deleteSourceAfterMigration)
                {
                    try
                    {
                        Directory.Delete(currentFull, recursive: true);
                        StatusMessage = Localization.MigrateSuccessAndSourceDeleted;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to delete source directory after migration: {ex.Message}");
                        StatusMessage = Localization.MigrateSuccessButSourceDeleteFailed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = Localization.MigrateCancelled;
            }
            catch (Exception ex)
            {
                Logger.Error($"Game migration failed: {ex.Message}");
                StatusMessage = Localization.MigrateFailed;
            }
            finally
            {
                _isBusy = false;
                _migrationCts?.Dispose();
                _migrationCts = null;
            }
        }

        [RelayCommand]
        private void CancelMigration()
        {
            _isCancellationRequested = true;
            _migrationCts?.Cancel();
        }

        private async UniTask CopyDirectoryAsync(string sourceDir, string targetDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetDir);

            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            var total = files.Length;
            var count = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                count++;
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var destFile = Path.Combine(targetDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, overwrite: true);

                Progress = (double)count / total * 100;
                StatusMessage = Cysharp.Text.ZString.Format("{0} ({1}/{2})", Localization.MigrateCopyingFiles, count, total);
                await UniTask.Yield(cancellationToken);
            }
        }
    }
}

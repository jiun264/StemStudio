using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using StemStudio.Core;
using StemStudio.Infrastructure;
using System.Windows;
using System.Windows.Controls;

namespace StemStudio.App;

public partial class MainWindow : Window
{
    private const long RecommendedFreeSpaceBytes = 1024L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aac"
        };

    private readonly ObservableCollection<string> _inputFiles = [];
    private readonly IStemStudioSettingsStore _settingsStore = new StemStudioSettingsStore();
    private StemStudioSettings _settings;
    private DemucsSeparationService _separationService;
    private CancellationTokenSource? _processingCancellation;

    public MainWindow()
    {
        try
        {
            _settings = _settingsStore.Load();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"無法讀取 StemStudio 設定：{exception.Message}",
                "設定錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }

        _separationService = new DemucsSeparationService(_settings);
        InitializeComponent();
        FileList.ItemsSource = _inputFiles;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var cancellation = new CancellationTokenSource();
        _processingCancellation = cancellation;
        SetProcessingState(true);
        try
        {
            if (await _separationService.IsModelReadyAsync(cancellation.Token))
            {
                StatusText.Text = "模型已就緒。分軌結果會儲存在設定中的輸出資料夾。";
                return;
            }

            StatusText.Text = "首次使用需要下載 AI 模型。";
            await PrepareModelIfNeededAsync(cancellation.Token, askBeforeDownload: true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "模型下載已取消；下次開始分軌時可重新下載。";
        }
        catch (Exception exception)
        {
            StatusText.Text = "無法檢查或下載模型。";
            MessageBox.Show(this, exception.Message, "模型初始化失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            cancellation.Dispose();
            _processingCancellation = null;
            SetProcessingState(false);
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇音訊檔案",
            Filter = "音訊檔案|*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.wma;*.aac|所有檔案|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddInputFiles(dialog.FileNames);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is string selectedFile)
        {
            _inputFiles.Remove(selectedFile);
            StatusText.Text = "已移除選取的檔案。";
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled = FileList.SelectedItem is not null && _processingCancellation is null;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_processingCancellation is not null)
        {
            StatusText.Text = "請等待目前工作完成後再加入檔案。";
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddInputFiles(paths);
        }
    }

    private void AddInputFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var unsupported = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                unsupported++;
                continue;
            }

            if (!_inputFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _inputFiles.Add(path);
                added++;
            }
        }

        StatusText.Text = unsupported > 0
            ? $"已加入 {added} 個檔案；略過 {unsupported} 個不存在或不支援的檔案。"
            : $"目前共有 {_inputFiles.Count} 個待處理檔案。";
    }

    private async void SeparateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inputFiles.Count == 0)
        {
            MessageBox.Show(this, "請先選擇或拖放音訊檔案。", "尚未加入檔案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedStems = GetSelectedStems();
        if (selectedStems.Count == 0)
        {
            MessageBox.Show(this, "請至少選擇一種要輸出的音軌。", "尚未選擇音軌", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _processingCancellation = cancellation;
        SetProcessingState(true);
        var progress = new Progress<string>(message => StatusText.Text = message);
        var completed = 0;
        string? lastOutputDirectory = null;

        try
        {
            if (!await PrepareModelIfNeededAsync(cancellation.Token, askBeforeDownload: true))
            {
                return;
            }

            foreach (var inputFile in _inputFiles.ToArray())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                StatusText.Text = $"正在分離：{Path.GetFileName(inputFile)}";
                var result = await _separationService.SeparateAsync(
                    new SeparationRequest(inputFile, selectedStems),
                    progress,
                    cancellation.Token);
                completed++;
                lastOutputDirectory = result.OutputDirectory;
            }

            StatusText.Text = $"完成 {completed} 個檔案。輸出位置：{lastOutputDirectory}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = $"已取消。已完成 {completed} 個檔案。";
        }
        catch (Exception exception)
        {
            StatusText.Text = "分軌失敗。";
            MessageBox.Show(this, exception.Message, "分軌失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            cancellation.Dispose();
            _processingCancellation = null;
            SetProcessingState(false);
            RemoveButton.IsEnabled = FileList.SelectedItem is not null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _processingCancellation?.Cancel();
        StatusText.Text = "正在取消目前的分軌工作…";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.UpdatedSettings is not { } updatedSettings)
        {
            return;
        }

        try
        {
            _settingsStore.Save(updatedSettings);
            _settings = updatedSettings;
            _separationService = new DemucsSeparationService(_settings);
            StatusText.Text = "設定已儲存。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "無法儲存設定", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> PrepareModelIfNeededAsync(
        CancellationToken cancellationToken,
        bool askBeforeDownload)
    {
        if (await _separationService.IsModelReadyAsync(cancellationToken))
        {
            return true;
        }

        if (!askBeforeDownload)
        {
            return false;
        }

        var modelRoot = Path.GetPathRoot(Path.GetFullPath(_settings.ModelCacheDirectory))
            ?? throw new InvalidOperationException("無法判斷模型資料夾所在磁碟。");
        var drive = new DriveInfo(modelRoot);
        var availableBytes = drive.AvailableFreeSpace;
        var availableGigabytes = availableBytes / (1024d * 1024 * 1024);
        var lowSpace = availableBytes < RecommendedFreeSpaceBytes;
        var prompt = lowSpace
            ? $"模型需要下載至「{_settings.ModelCacheDirectory}」。目前磁碟可用空間約 {availableGigabytes:F1} GB，建議至少預留 1 GB 供模型快取與音訊輸出。空間不足可能導致工作失敗。仍要繼續嗎？"
            : $"首次使用需要下載 Demucs htdemucs_6s 模型（目前權重約 55 MB；建議至少預留 1 GB 供模型快取與音訊輸出）。模型會儲存在「{_settings.ModelCacheDirectory}」，只下載一次。現在下載嗎？";
        var answer = MessageBox.Show(
            this,
            prompt,
            "下載分軌模型",
            MessageBoxButton.YesNo,
            lowSpace ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "已略過模型下載；開始分軌時可以再下載。";
            return false;
        }

        await _separationService.PrepareModelAsync(
            new Progress<string>(message => StatusText.Text = message),
            cancellationToken);
        StatusText.Text = "模型下載完成，已準備分軌。";
        return true;
    }

    private IReadOnlyCollection<SeparationStem> GetSelectedStems()
    {
        var stems = new List<SeparationStem>();
        if (VocalsCheckBox.IsChecked == true)
        {
            stems.Add(SeparationStem.Vocals);
        }

        if (GuitarCheckBox.IsChecked == true)
        {
            stems.Add(SeparationStem.Guitar);
        }

        if (BassCheckBox.IsChecked == true)
        {
            stems.Add(SeparationStem.Bass);
        }

        if (DrumsCheckBox.IsChecked == true)
        {
            stems.Add(SeparationStem.Drums);
        }

        return stems;
    }

    private void SetProcessingState(bool isProcessing)
    {
        SeparateButton.IsEnabled = !isProcessing;
        CancelButton.IsEnabled = isProcessing;
        SettingsButton.IsEnabled = !isProcessing;
        BrowseButton.IsEnabled = !isProcessing;
        StemOptions.IsEnabled = !isProcessing;
        FileList.IsEnabled = !isProcessing;
        ProgressIndicator.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
        ProgressIndicator.IsIndeterminate = isProcessing;
        RemoveButton.IsEnabled = !isProcessing && FileList.SelectedItem is not null;
    }
}

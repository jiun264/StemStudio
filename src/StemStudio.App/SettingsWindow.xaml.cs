using System.IO;
using Microsoft.Win32;
using StemStudio.Core;
using System.Windows;

namespace StemStudio.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(StemStudioSettings settings)
    {
        InitializeComponent();
        OutputDirectoryTextBox.Text = settings.OutputDirectory;
        ModelCacheDirectoryTextBox.Text = settings.ModelCacheDirectory;
    }

    public StemStudioSettings? UpdatedSettings { get; private set; }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseForDirectory(OutputDirectoryTextBox, "選擇分軌輸出資料夾");
    }

    private void BrowseModelButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseForDirectory(ModelCacheDirectoryTextBox, "選擇模型快取資料夾");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text) ||
            string.IsNullOrWhiteSpace(ModelCacheDirectoryTextBox.Text))
        {
            MessageBox.Show(this, "兩個資料夾路徑都必須填寫。", "設定不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            UpdatedSettings = new StemStudioSettings(
                Path.GetFullPath(OutputDirectoryTextBox.Text.Trim()),
                Path.GetFullPath(ModelCacheDirectoryTextBox.Text.Trim()));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "資料夾路徑無效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BrowseForDirectory(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
        }
    }
}

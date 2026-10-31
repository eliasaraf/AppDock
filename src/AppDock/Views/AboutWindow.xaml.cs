using System.IO;
using System.Windows;
using AppDock.Services;

namespace AppDock.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        CopyrightText.Text = AppInfo.Copyright;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => AppInfo.OpenFolder(AppInfo.LogsDirectory);

    private void OnLicense(object sender, RoutedEventArgs e)
    {
        var license = Path.Combine(System.AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(license))
        {
            AppInfo.OpenUrl(license);
        }
        else
        {
            MessageBox.Show(this, AppInfo.Copyright, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

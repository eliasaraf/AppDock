using System.Windows;

namespace AppDock.Views;

/// <summary>First-run welcome: explains the three gestures and picks the app to dock.</summary>
public partial class OnboardingWindow : Window
{
    public OnboardingWindow() => InitializeComponent();

    public bool PickOtherApp => OtherChoice.IsChecked == true;

    public bool StartWithWindows => StartupBox.IsChecked == true;

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

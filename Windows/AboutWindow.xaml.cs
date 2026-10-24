using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ShowCPU.Services;

namespace ShowCPU;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionRun.Text = "Version " + VersionText();
        WindowEffects.Attach(this);
    }

    private static string VersionText()
    {
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            return assemblyVersion == null
                ? "1.0.0"
                : assemblyVersion.Major + "." + assemblyVersion.Minor + "." + assemblyVersion.Build;
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            version = version[..plus];
        return version;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            WindowEffects.Drag(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

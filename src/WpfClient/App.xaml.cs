using System.Windows;

namespace WpfClient;

public partial class App : Application
{
    public static Api Api { get; } = new();

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
    }
}

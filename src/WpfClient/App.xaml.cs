using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfClient;

public partial class App : Application
{
    public static Api Api { get; } = new();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MsgDialog.Info(MainWindow, "Ошибка", e.Exception.Message);
            e.Handled = true;
        };
        // Тёмная системная рамка окна (Windows 10 20H1+ / 11) для всех окон приложения.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            try
            {
                var h = new WindowInteropHelper((Window)s).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, sizeof(int));
            }
            catch { }
        }));
    }
}

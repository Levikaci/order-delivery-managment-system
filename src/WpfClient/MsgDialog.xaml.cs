using System.Windows;

namespace WpfClient;

/// <summary>Тёмное окно сообщения и подтверждения (вместо системного MessageBox).</summary>
public partial class MsgDialog : Window
{
    private MsgDialog(string head, string body, bool confirm, string yes)
    {
        InitializeComponent();
        Title = head; Head.Text = head; Body.Text = body;
        Body.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Yes.Content = yes;
        if (!confirm) No.Visibility = Visibility.Collapsed;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static bool Run(Window? owner, string head, string body, bool confirm, string yes)
    {
        var d = new MsgDialog(head, body, confirm, yes);
        owner ??= Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true;
    }

    public static void Info(Window? owner, string head, string body = "") => Run(owner, head, body, false, "OK");
    public static bool Confirm(Window? owner, string head, string body = "", string yes = "Да") => Run(owner, head, body, true, yes);
}

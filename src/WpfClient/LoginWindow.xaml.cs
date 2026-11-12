using System.Windows;
using System.Windows.Controls;

namespace WpfClient;

public partial class LoginWindow : Window
{
    public LoginWindow() { InitializeComponent(); Loaded += (_, _) => Login.Focus(); }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        var l = (string)((Button)sender).Tag;
        Login.Text = l; Password.Password = l;
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        Error.Text = ""; Go.IsEnabled = false;
        try
        {
            App.Api.SetServer(Server.Text.Trim());
            await App.Api.LoginAsync(Login.Text.Trim(), Password.Password);
            OpenMain();
        }
        catch (ApiException ex) { Error.Text = ex.Message; }
        catch (UriFormatException) { Error.Text = "Некорректный адрес сервера"; }
        finally { Go.IsEnabled = true; }
    }

    private void OpenMain()
    {
        var main = new MainWindow();
        Application.Current.MainWindow = main;
        main.Show();
        Close();
    }

    /// <summary>Регистрация нового клиента на указанном сервере; после успешной регистрации вход выполняется сразу.</summary>
    private void Register_Click(object sender, RoutedEventArgs e)
    {
        Error.Text = "";
        try { App.Api.SetServer(Server.Text.Trim()); }
        catch (UriFormatException) { Error.Text = "Некорректный адрес сервера"; return; }
        if (new RegisterDialog(this).ShowDialog() == true) OpenMain();
    }
}

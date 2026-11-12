using System.Windows;

namespace WpfClient;

/// <summary>Самостоятельная регистрация клиента (POST /api/auth/register).</summary>
public partial class RegisterDialog : Window
{
    public RegisterDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        Loaded += (_, _) => NameBox.Focus();
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        Error.Text = "";
        if (NameBox.Text.Trim().Length == 0) { Error.Text = "Укажите имя"; NameBox.Focus(); return; }
        if (LoginBox.Text.Trim().Length == 0) { Error.Text = "Укажите логин"; LoginBox.Focus(); return; }
        if (PasswordBox.Password != RepeatBox.Password) { Error.Text = "Пароли не совпадают"; RepeatBox.Focus(); return; }
        Ok.IsEnabled = false;
        try
        {
            var phone = PhoneBox.Text.Trim();
            await App.Api.RegisterAsync(NameBox.Text.Trim(), phone.Length == 0 ? null : phone, LoginBox.Text.Trim(), PasswordBox.Password);
            DialogResult = true;
        }
        catch (ApiException ex) { Error.Text = ex.Message; }
        finally { Ok.IsEnabled = true; }
    }
}

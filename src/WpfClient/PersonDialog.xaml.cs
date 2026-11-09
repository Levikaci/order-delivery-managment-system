using System.Windows;

namespace WpfClient;

/// <summary>Добавление клиента, курьера (имя, телефон, логин, пароль) или товара (название, цена).</summary>
public partial class PersonDialog : Window
{
    public string V1 => F1.Text.Trim();
    public string V2 => F2.Text.Trim();
    public string V3 => F3.Text.Trim();
    public string V4 => F4.Text.Trim();

    public PersonDialog(Window owner, string title, bool product = false)
    {
        InitializeComponent();
        Owner = owner; Title = title;
        if (product) { L1.Content = "Название"; L2.Content = "Цена, ₽"; Creds.Visibility = Visibility.Collapsed; }
        F1.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { if (V1.Length > 0) DialogResult = true; else F1.Focus(); }
}

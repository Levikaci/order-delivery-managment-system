using System.Windows;

namespace WpfClient;

/// <summary>Добавление и редактирование клиента, курьера (имя, телефон, логин, пароль) или товара (название, цена).</summary>
public partial class PersonDialog : Window
{
    public string V1 => F1.Text.Trim();
    public string V2 => F2.Text.Trim();
    public string V3 => F3.Text.Trim();
    public string V4 => F4.Text.Trim();

    public PersonDialog(Window owner, string title, bool product = false, string v1 = "", string v2 = "", bool edit = false)
    {
        InitializeComponent();
        Owner = owner; Title = title; Head.Text = title;
        F1.Text = v1; F2.Text = v2;
        if (product) { L1.Text = "НАЗВАНИЕ"; L2.Text = "ЦЕНА, ₽"; }
        if (product || edit) Creds.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { F1.Focus(); F1.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { if (V1.Length > 0) DialogResult = true; else F1.Focus(); }
}

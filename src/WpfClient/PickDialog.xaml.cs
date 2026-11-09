using System.Windows;

namespace WpfClient;

/// <summary>Универсальный диалог: выбор из списка и/или ввод текста (например, причина неудачи).</summary>
public partial class PickDialog : Window
{
    public string? Selected { get; private set; }
    public string Text => Input.Text.Trim();
    private readonly List<(string value, string label)> _items;
    private readonly bool _needText;

    public PickDialog(Window owner, string title, string prompt, IEnumerable<(string value, string label)> items, bool needText = false)
    {
        InitializeComponent();
        Owner = owner; Title = title; Head.Text = title; Prompt.Text = prompt;
        _items = items.ToList(); _needText = needText;
        foreach (var i in _items) List.Items.Add(i.label);
        if (_items.Count > 0) List.SelectedIndex = 0; else List.Visibility = Visibility.Collapsed;
        if (needText) Input.Visibility = Visibility.Visible;
        if (_items.Count == 0) Ok.Content = "Готово";
    }

    private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (List.SelectedIndex >= 0) Ok_Click(sender, e); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0 && List.SelectedIndex < 0) return;
        if (_needText && Text.Length == 0) { Input.Focus(); return; }
        Selected = _items.Count > 0 ? _items[List.SelectedIndex].value : null;
        DialogResult = true;
    }
}

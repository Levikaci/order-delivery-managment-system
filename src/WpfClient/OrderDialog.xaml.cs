using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace WpfClient;

public class ProductRow : INotifyPropertyChanged
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public decimal Price { get; init; }
    public string PriceText => Price.ToString("0.##");
    private int _q;
    public int Quantity { get => _q; set { _q = Math.Clamp(value, 0, 99); PropertyChanged?.Invoke(this, new(nameof(Quantity))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Создание и редактирование заказа: товары с количеством, сохранённый или новый адрес, комментарий.</summary>
public partial class OrderDialog : Window
{
    private readonly List<ProductRow> _rows = new();
    private readonly List<JsonNode> _addresses;
    public Guid CustomerId { get; }

    public OrderDialog(Window owner, JsonArray products, JsonArray addresses, Guid customerId, JsonNode? existing = null)
    {
        InitializeComponent();
        Owner = owner; CustomerId = customerId;
        Title = existing is null ? "Новый заказ" : "Изменение заказа"; Head.Text = Title;
        if (existing is not null) Ok.Content = "Сохранить";
        foreach (var p in products)
        {
            var id = Guid.Parse(p!["id"]!.GetValue<string>());
            var q = existing?["items"]?.AsArray().FirstOrDefault(i => i!["productId"]!.GetValue<string>() == id.ToString())?["quantity"]?.GetValue<int>() ?? 0;
            _rows.Add(new ProductRow { Id = id, Name = p["name"]!.GetValue<string>(), Price = p["price"]!.GetValue<decimal>(), Quantity = q });
        }
        Items.ItemsSource = _rows;
        _addresses = addresses.Select(a => a!).ToList();
        foreach (var a in _addresses) Saved.Items.Add(a["text"]?.ToString() ?? $"{a["city"]}, {a["street"]}, {a["house"]}");
        if (_addresses.Count > 0 && existing is null) Saved.SelectedIndex = 0;
        Comment.Text = existing?["comment"]?.ToString() ?? "";
        UpdateTotal();
    }

    private void UpdateTotal() => Total.Text = _rows.Sum(r => r.Price * r.Quantity).ToString("#,0.##") + " ₽";
    private void Inc_Click(object sender, RoutedEventArgs e) { ((ProductRow)((Button)sender).DataContext).Quantity++; UpdateTotal(); }
    private void Dec_Click(object sender, RoutedEventArgs e) { ((ProductRow)((Button)sender).DataContext).Quantity--; UpdateTotal(); }
    private void NewAddr_Click(object sender, RoutedEventArgs e) { Saved.SelectedIndex = -1; City.Focus(); }

    private async void DelAddr_Click(object sender, RoutedEventArgs e)
    {
        if (Saved.SelectedIndex < 0) return;
        try
        {
            await App.Api.DeleteAsync("api/addresses/" + _addresses[Saved.SelectedIndex]["id"]);
            _addresses.RemoveAt(Saved.SelectedIndex); Saved.Items.RemoveAt(Saved.SelectedIndex);
        }
        catch (ApiException ex) { MsgDialog.Info(this, ex.Message); }
    }

    /// <summary>Тело запроса POST /api/orders или PUT /api/orders/{id}.</summary>
    public Dictionary<string, object?> Body { get; private set; } = new();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var items = _rows.Where(r => r.Quantity > 0).Select(r => new { productId = r.Id, quantity = r.Quantity }).ToList();
        if (items.Count == 0) { MsgDialog.Info(this, "Добавьте в заказ хотя бы один товар"); return; }
        var body = new Dictionary<string, object?> { ["customerId"] = CustomerId, ["items"] = items, ["comment"] = Comment.Text.Trim() };
        if (Saved.SelectedIndex >= 0) body["addressId"] = Guid.Parse(_addresses[Saved.SelectedIndex]["id"]!.GetValue<string>());
        else
        {
            if (City.Text.Trim() == "" || Street.Text.Trim() == "" || House.Text.Trim() == "") { MsgDialog.Info(this, "Укажите город, улицу и дом"); return; }
            body["deliveryAddress"] = new { city = City.Text.Trim(), street = Street.Text.Trim(), house = House.Text.Trim(), apartment = Apt.Text.Trim() };
            body["saveAddress"] = Save.IsChecked == true;
        }
        Body = body; DialogResult = true;
    }
}

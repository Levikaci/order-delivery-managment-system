using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace WpfClient;

public class DeliveryRow
{
    public string Id { get; init; } = "";
    public string Num { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Address { get; init; } = "";
    public string Courier { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Total { get; init; } = "";
    public string Created { get; init; } = "";
    public JsonNode Node { get; init; } = null!;
}

public class DirRow { public string Id { get; init; } = ""; public string A { get; init; } = ""; public string B { get; init; } = ""; public string C { get; init; } = ""; public string State { get; init; } = ""; }

public partial class MainWindow : Window
{
    private static Api Api => App.Api;
    private JsonNode _state = new JsonObject();
    private string _view = "deliveries";
    private readonly Dictionary<string, ToggleButton> _nav = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private string? _selectedId;

    public MainWindow()
    {
        InitializeComponent();
        var role = Api.Role;
        Who.Text = $"{Api.UserName} · {(role == "operator" ? "оператор" : role == "client" ? "клиент" : "курьер")}";
        if (role == "operator")
        {
            AddNav("deliveries", "Диспетчерская"); AddNav("customers", "Клиенты"); AddNav("couriers", "Курьеры"); AddNav("products", "Товары");
        }
        else AddNav("deliveries", role == "client" ? "Мои заказы" : "Маршрутный лист");
        _nav["deliveries"].IsChecked = true;
        _timer.Tick += async (_, _) => { try { await Reload(); } catch { } };
        Loaded += async (_, _) => { await Safe(Reload); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void AddNav(string key, string title)
    {
        var b = new ToggleButton { Content = title, Style = (Style)FindResource("Nav") };
        b.Click += (_, _) => { _view = key; foreach (var n in _nav) n.Value.IsChecked = n.Key == key; ShowView(); };
        _nav[key] = b; Nav.Children.Add(b);
    }

    private async Task Safe(Func<Task> f)
    {
        try { await f(); }
        catch (ApiException ex) { MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task Reload()
    {
        _state = (await Api.GetAsync("api/state"))!;
        ShowView();
    }

    private void ShowView()
    {
        DeliveriesView.Visibility = _view == "deliveries" ? Visibility.Visible : Visibility.Collapsed;
        DirView.Visibility = _view == "deliveries" ? Visibility.Collapsed : Visibility.Visible;
        if (_view == "deliveries") FillDeliveries(); else FillDirectory();
    }

    // ---------- справочники и выборки
    private JsonArray Arr(string name) => _state[name]?.AsArray() ?? new JsonArray();
    private string NameOf(string coll, string? id) => id is null ? "—" : Arr(coll).FirstOrDefault(x => x!["id"]!.ToString() == id)?["name"]?.ToString() ?? "—";
    private static string Dt(JsonNode? n) => n is null ? "—" : DateTime.TryParse(n.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—";

    // ---------- доставки
    private void FillDeliveries()
    {
        var q = Search.Text.Trim().ToLowerInvariant();
        var list = new List<DeliveryRow>();
        foreach (var d in Arr("deliveries"))
        {
            var row = new DeliveryRow
            {
                Id = d!["deliveryId"]!.ToString(), Num = d["orderId"]!.ToString()[..8].ToUpperInvariant(),
                Customer = NameOf("customers", d["customerId"]?.ToString()), Address = d["address"]?.ToString() ?? "",
                Courier = NameOf("couriers", d["courierId"]?.ToString()), Status = d["status"]!.ToString(), StatusText = Texts.D(d["status"]!.ToString()),
                Total = d["total"]?.ToString() ?? "0", Created = Dt(d["createdAt"]), Node = d
            };
            var hay = $"{row.Num} {row.Customer} {row.Address} {row.Courier} {row.StatusText} {d["description"]}".ToLowerInvariant();
            if (q.Length == 0 || hay.Contains(q)) list.Add(row);
        }
        list = list.OrderByDescending(r => r.Node["createdAt"]!.ToString()).ToList();
        Grid.ItemsSource = new ObservableCollection<DeliveryRow>(list);
        Counter.Text = $"Записей: {list.Count}";
        var sel = list.FirstOrDefault(r => r.Id == _selectedId);
        if (sel != null) Grid.SelectedItem = sel;
        ShowDetails(); BuildActions();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded && _view == "deliveries") FillDeliveries(); }
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is DeliveryRow r) _selectedId = r.Id;
        ShowDetails(); BuildActions();
    }

    private DeliveryRow? Sel => Grid.SelectedItem as DeliveryRow;

    private void ShowDetails()
    {
        if (Sel is not { } r) { Details.Text = "Выберите заказ в списке"; return; }
        var d = r.Node; var sb = new StringBuilder();
        sb.AppendLine($"Заказ № {r.Num}");
        sb.AppendLine($"Статус заказа: {Texts.O(d["orderStatus"]?.ToString())}");
        sb.AppendLine($"Статус доставки: {r.StatusText}");
        sb.AppendLine($"Клиент: {r.Customer}");
        sb.AppendLine($"Адрес: {r.Address}");
        sb.AppendLine($"Курьер: {r.Courier}");
        sb.AppendLine().AppendLine("Состав:");
        foreach (var i in d["items"]?.AsArray() ?? new JsonArray())
            sb.AppendLine($"  {i!["name"]} × {i["quantity"]} — {i["price"]} ₽");
        sb.AppendLine($"Итого: {r.Total} ₽");
        if (!string.IsNullOrWhiteSpace(d["comment"]?.ToString())) sb.AppendLine($"Комментарий: {d["comment"]}");
        sb.AppendLine().AppendLine($"Создан: {r.Created}");
        sb.AppendLine($"Начата: {Dt(d["startedAt"])}");
        sb.AppendLine($"Завершена: {Dt(d["finishedAt"])}");
        if (d["confirmedAt"] != null) sb.AppendLine($"Получение подтверждено: {Dt(d["confirmedAt"])}");
        if (!string.IsNullOrWhiteSpace(d["failureReason"]?.ToString())) sb.AppendLine($"Причина неудачи: {d["failureReason"]}");
        sb.AppendLine().AppendLine("История доставки:");
        foreach (var h in d["history"]?.AsArray() ?? new JsonArray())
            sb.AppendLine($"  {Dt(h!["at"])}  {Texts.D(h["status"]!.ToString())}" + (string.IsNullOrWhiteSpace(h["comment"]?.ToString()) ? "" : $" — {h["comment"]}"));
        Details.Text = sb.ToString();
    }

    // ---------- кнопки действий по роли
    private void Btn(Panel host, string text, Func<Task> act, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
        if (primary) b.Style = (Style)FindResource("Primary");
        b.Click += async (_, _) => await Safe(async () => { await act(); await Reload(); });
        host.Children.Add(b);
    }

    private void BuildActions()
    {
        Actions.Children.Clear();
        var role = Api.Role; var r = Sel; var st = r?.Status;
        if (role == "operator" && r != null)
        {
            if (st == "Created") Btn(Actions, "Назначить курьера", AssignAsync, true);
            var next = Texts.Next.GetValueOrDefault(st ?? "")?.Where(s => s != "Assigned").ToArray() ?? Array.Empty<string>();
            if (next.Length > 0) Btn(Actions, "Сменить статус", () => OperatorStatusAsync(next));
            if (st == "Delivered" && r.Node["confirmedAt"] == null) Btn(Actions, "Подтвердить получение", () => Api.PostAsync($"api/deliveries/{r.Id}/confirm"));
        }
        else if (role == "client")
        {
            Btn(Actions, "Новый заказ", () => OrderAsync(null), true);
            if (r != null && st is "Created" or "Assigned") { Btn(Actions, "Изменить", () => OrderAsync(r.Node)); Btn(Actions, "Отменить заказ", () => SetStatus(r.Id, "Cancelled", null)); }
            if (r != null && st == "Delivered" && r.Node["confirmedAt"] == null) Btn(Actions, "Подтвердить получение", () => Api.PostAsync($"api/deliveries/{r.Id}/confirm"), true);
        }
        else if (role == "courier")
        {
            var me = Arr("couriers").FirstOrDefault();
            var cs = me?["state"]?.ToString();
            if (r != null)
            {
                if (st == "Assigned") Btn(Actions, "Принять", () => SetStatus(r.Id, "Accepted", null), true);
                if (st == "Accepted") Btn(Actions, "В пути", () => SetStatus(r.Id, "InTransit", null), true);
                if (st == "InTransit") Btn(Actions, "Доставлено", () => SetStatus(r.Id, "Delivered", null), true);
                if (st is "Assigned" or "Accepted" or "InTransit") Btn(Actions, "Не удалось", () => FailAsync(r.Id));
            }
            if (cs is "Available" or "Offline")
                Btn(Actions, cs == "Available" ? "Уйти с линии" : "Выйти на линию", () => Api.PatchAsync($"api/couriers/{Api.RefId}/state", new { state = cs == "Available" ? "Offline" : "Available" }));
            if (cs != null) Actions.Children.Add(new TextBlock { Text = "Моё состояние: " + Texts.C(cs), Style = (Style)FindResource("MutedText"), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private Task SetStatus(string id, string status, string? comment) => Api.PatchAsync($"api/deliveries/{id}/status", new { status, comment });

    private async Task FailAsync(string id)
    {
        var dlg = new PickDialog(this, "Доставка не удалась", "Укажите причину", Array.Empty<(string, string)>(), true);
        if (dlg.ShowDialog() == true) await SetStatus(id, "Failed", dlg.Text);
    }

    private async Task AssignAsync()
    {
        var items = new List<(string, string)> { ("", "Автоматически (первый свободный)") };
        items.AddRange(Arr("couriers").Where(c => c!["state"]!.ToString() == "Available").Select(c => (c!["id"]!.ToString(), c["name"]!.ToString())));
        var dlg = new PickDialog(this, "Назначение курьера", "Выберите курьера", items);
        if (dlg.ShowDialog() != true) return;
        object body = string.IsNullOrEmpty(dlg.Selected) ? new { } : new { courierId = dlg.Selected };
        await Api.PostAsync($"api/deliveries/{Sel!.Id}/assign", body);
    }

    private async Task OperatorStatusAsync(string[] next)
    {
        var dlg = new PickDialog(this, "Смена статуса", "Новый статус доставки", next.Select(s => (s, Texts.D(s))));
        if (dlg.ShowDialog() != true) return;
        if (dlg.Selected == "Failed") await FailAsync(Sel!.Id); else await SetStatus(Sel!.Id, dlg.Selected!, null);
    }

    private async Task OrderAsync(JsonNode? existing)
    {
        var products = (await Api.GetAsync("api/products"))!.AsArray();
        var addresses = Arr("addresses");
        var dlg = new OrderDialog(this, products, addresses, Api.RefId!.Value, existing);
        if (dlg.ShowDialog() != true) return;
        if (existing is null)
        {
            var r = await Api.PostAsync("api/orders", dlg.Body);
            MessageBox.Show(this, r?["message"]?.ToString() ?? "Заказ создан", "Заказ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else await Api.PutAsync($"api/orders/{existing["orderId"]}", dlg.Body);
    }

    // ---------- справочники оператора
    private void FillDirectory()
    {
        DirGrid.Columns.Clear(); DirActions.Children.Clear();
        var rows = new List<DirRow>();
        void Col(string h, string path, double w = 0) => DirGrid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(path), Width = w > 0 ? new DataGridLength(w) : new DataGridLength(1, DataGridLengthUnitType.Star) });
        if (_view == "customers")
        {
            Col("Имя", "A"); Col("Телефон", "B", 200);
            rows = Arr("customers").Select(c => new DirRow { Id = c!["id"]!.ToString(), A = c["name"]!.ToString(), B = c["phone"]?.ToString() ?? "" }).ToList();
        }
        else if (_view == "couriers")
        {
            Col("Имя", "A"); Col("Телефон", "B", 200); Col("Состояние", "C", 160);
            rows = Arr("couriers").Select(c => new DirRow { Id = c!["id"]!.ToString(), A = c["name"]!.ToString(), B = c["phone"]?.ToString() ?? "", C = Texts.C(c["state"]!.ToString()), State = c["state"]!.ToString() }).ToList();
        }
        else
        {
            Col("Название", "A"); Col("Цена, ₽", "B", 140);
            rows = Arr("products").Select(p => new DirRow { Id = p!["id"]!.ToString(), A = p["name"]!.ToString(), B = p["price"]!.ToString() }).ToList();
        }
        DirGrid.ItemsSource = rows;
        Btn(DirActions, "Добавить", AddEntryAsync, true);
        Btn(DirActions, "Удалить выбранного", DeleteEntryAsync);
        if (_view == "couriers") Btn(DirActions, "Изменить состояние", CourierStateAsync);
    }

    private async Task AddEntryAsync()
    {
        var product = _view == "products";
        var dlg = new PersonDialog(this, _view == "customers" ? "Новый клиент" : _view == "couriers" ? "Новый курьер" : "Новый товар", product);
        if (dlg.ShowDialog() != true) return;
        JsonNode? r;
        if (product)
        {
            if (!decimal.TryParse(dlg.V2.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price)) { MessageBox.Show(this, "Укажите цену числом"); return; }
            r = await Api.PostAsync("api/products", new { name = dlg.V1, price });
        }
        else
        {
            r = await Api.PostAsync("api/" + _view, new { name = dlg.V1, phone = dlg.V2, login = dlg.V3.Length > 0 ? dlg.V3 : null, password = dlg.V4.Length > 0 ? dlg.V4 : null });
            var login = r?["login"]?.ToString() ?? r?["credentials"]?["login"]?.ToString();
            var pass = r?["password"]?.ToString() ?? r?["credentials"]?["password"]?.ToString();
            if (login != null) MessageBox.Show(this, $"Учётная запись создана\n\nЛогин: {login}\nПароль: {pass}", "Данные для входа", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task DeleteEntryAsync()
    {
        if (DirGrid.SelectedItem is not DirRow r) return;
        if (MessageBox.Show(this, $"Удалить «{r.A}»?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Api.DeleteAsync($"api/{_view}/{r.Id}");
    }

    private async Task CourierStateAsync()
    {
        if (DirGrid.SelectedItem is not DirRow r) return;
        var dlg = new PickDialog(this, "Состояние курьера", r.A, new[] { ("Available", "Свободен"), ("Offline", "Не на линии"), ("Blocked", "Заблокирован") });
        if (dlg.ShowDialog() == true) await Api.PatchAsync($"api/couriers/{r.Id}/state", new { state = dlg.Selected });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Safe(Reload);

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        await Api.LogoutAsync();
        var l = new LoginWindow(); Application.Current.MainWindow = l; l.Show(); Close();
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WpfClient;

public class DeliveryRow
{
    public string Id { get; init; } = "";
    public string Num { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Address { get; init; } = "";
    public string Courier { get; init; } = "";
    public string Initials { get; init; } = "";
    public Visibility ChipVisibility { get; init; } = Visibility.Collapsed;
    public Brush CourierBrush { get; init; } = Brushes.Gray;
    public string Status { get; init; } = "";
    public string StatusText { get; init; } = "";
    public Brush StatusFg { get; init; } = Brushes.Gray;
    public Brush StatusBg { get; init; } = Brushes.Transparent;
    public string Total { get; init; } = "";
    public string Created { get; init; } = "";
    public string Cat { get; init; } = "";
    public JsonNode Node { get; init; } = null!;
}

public class DirRow
{
    public string Id { get; init; } = ""; public string A { get; init; } = ""; public string B { get; init; } = ""; public string C { get; init; } = ""; public string D { get; init; } = "";
    public string State { get; init; } = ""; public string E { get; init; } = ""; public string F { get; init; } = ""; public string G { get; init; } = ""; public string H { get; init; } = "";
    public Brush StatusFg { get; init; } = Brushes.Gray; public Brush StatusBg { get; init; } = Brushes.Transparent;
}

public partial class MainWindow : Window
{
    private static Api Api => App.Api;
    private JsonNode _state = new JsonObject();
    private string _view = "deliveries";
    private string _cat = "all";
    private readonly Dictionary<string, ToggleButton> _nav = new();
    private readonly Dictionary<string, ToggleButton> _tabs = new();
    private TextBlock? _navBadge;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private string? _selectedId;

    // ---------- оформление: цвета статусов
    private static readonly Dictionary<string, string> StatusColor = new()
    {
        ["Created"] = "#9AA4B2", ["Assigned"] = "#6FA0FF", ["Accepted"] = "#A794F6", ["InTransit"] = "#E0A93B",
        ["Delivered"] = "#4CB782", ["Failed"] = "#E5604D", ["Cancelled"] = "#7B848E",
        ["Available"] = "#4CB782", ["Busy"] = "#E0A93B", ["Offline"] = "#7B848E", ["Blocked"] = "#E5604D",
    };
    private static Color Col(string key) => (Color)ColorConverter.ConvertFromString(StatusColor.GetValueOrDefault(key, "#9AA4B2"));
    private static Brush Fg(string key) { var b = new SolidColorBrush(Col(key)); b.Freeze(); return b; }
    private static Brush Bgc(string key) { var c = Col(key); var b = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)); b.Freeze(); return b; }
    private Brush R(string key) => (Brush)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        var role = Api.Role;
        var roleText = role == "operator" ? "Оператор" : role == "client" ? "Клиент" : "Курьер";
        MeName.Text = Api.UserName; MeRole.Text = roleText; MeInitials.Text = Initials(Api.UserName);
        NavCaption("РАБОЧИЕ МЕСТА");
        if (role == "operator")
        {
            _navBadge = AddNav("deliveries", "Диспетчерская", true);
            NavCaption("ДАННЫЕ"); AddNav("customers", "Клиенты"); AddNav("couriers", "Курьеры"); AddNav("products", "Товары");
        }
        else AddNav("deliveries", role == "client" ? "Мои заказы" : "Маршрутный лист");
        _nav["deliveries"].IsChecked = true;
        BuildTabs();
        _timer.Tick += async (_, _) => { try { await Reload(); } catch { SetConn(false); } };
        Loaded += async (_, _) => { await Safe(Reload); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static string Initials(string? name)
    {
        var p = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? "—" : string.Concat(p.Take(2).Select(x => char.ToUpperInvariant(x[0])));
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var m = n % 100; if (m is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static string Money(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v.ToString("#,0.##", CultureInfo.GetCultureInfo("ru-RU")) + " ₽" : (raw ?? "0") + " ₽";

    private void NavCaption(string text) =>
        Nav.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Caption"), Margin = new Thickness(18, 16, 0, 6) });

    private TextBlock? AddNav(string key, string title, bool badge = false)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        TextBlock? bt = null;
        if (badge)
        {
            bt = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = R("Accent"), VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border { Background = R("AccentSoft"), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Right, Child = bt, Visibility = Visibility.Collapsed };
            chip.Tag = "badge"; content.Children.Add(chip);
        }
        var b = new ToggleButton { Content = content, Style = (Style)FindResource("NavItem") };
        b.Click += (_, _) => { _view = key; foreach (var n in _nav) n.Value.IsChecked = n.Key == key; ShowView(); };
        _nav[key] = b; Nav.Children.Add(b);
        return bt;
    }

    private void BuildTabs()
    {
        foreach (var (key, title) in new[] { ("all", "Все"), ("new", "Новые"), ("active", "Активные"), ("done", "Завершённые") })
        {
            var label = new TextBlock { Text = title, FontWeight = FontWeights.Medium };
            var count = new TextBlock { FontSize = 11, Foreground = R("Faint"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Name = null };
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(label); sp.Children.Add(count);
            var b = new ToggleButton { Content = sp, Style = (Style)FindResource("TabBtn"), Tag = count, IsChecked = key == "all" };
            b.Click += (_, _) => { _cat = key; foreach (var t in _tabs) t.Value.IsChecked = t.Key == key; FillDeliveries(); };
            _tabs[key] = b; Tabs.Children.Add(b);
        }
    }

    private void SetConn(bool ok)
    {
        ConnDot.Fill = R(ok ? "Ok" : "Danger");
        ConnText.Text = ok ? "Сервер подключён" : "Нет связи с сервером";
    }

    private async Task Safe(Func<Task> f)
    {
        try { await f(); }
        catch (ApiException ex) { MsgDialog.Info(this, "Не удалось выполнить действие", ex.Message); if (ex.Code == "NO_CONNECTION") SetConn(false); }
    }

    private async Task Reload()
    {
        _state = (await Api.GetAsync("api/state"))!;
        SetConn(true); Updated.Text = "Обновлено " + DateTime.Now.ToString("HH:mm:ss");
        ShowView();
    }

    private void ShowView()
    {
        var deliveries = _view == "deliveries";
        DeliveriesView.Visibility = deliveries ? Visibility.Visible : Visibility.Collapsed;
        DirView.Visibility = deliveries ? Visibility.Collapsed : Visibility.Visible;
        var role = Api.Role;
        Crumb1.Text = deliveries ? "Рабочие места" : "Данные";
        if (deliveries)
        {
            ViewTitle.Text = role == "operator" ? "Диспетчерская" : role == "client" ? "Мои заказы" : "Маршрутный лист";
            ViewSub.Text = role == "operator" ? "Все заказы, назначение курьеров и контроль выполнения" : role == "client" ? "Текущие доставки и история ваших заказов" : "Доставки, назначенные вам";
            Crumb2.Text = ViewTitle.Text;
            FillDeliveries();
        }
        else FillDirectory();
    }

    // ---------- справочники и выборки
    private JsonArray Arr(string name) => _state[name]?.AsArray() ?? new JsonArray();
    private JsonNode? Find(string coll, string? id) => id is null ? null : Arr(coll).FirstOrDefault(x => x!["id"]!.ToString() == id);
    private string NameOf(string coll, string? id) => Find(coll, id)?["name"]?.ToString() ?? "—";
    private static string Dt(JsonNode? n) => n is null ? "—" : DateTime.TryParse(n.ToString(), null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—";
    private static string Cat(string st) => st == "Created" ? "new" : st is "Assigned" or "Accepted" or "InTransit" ? "active" : "done";

    // ---------- доставки
    private void FillDeliveries()
    {
        var q = Search.Text.Trim().ToLowerInvariant();
        var all = new List<DeliveryRow>();
        foreach (var d in Arr("deliveries"))
        {
            var st = d!["status"]!.ToString();
            var courier = NameOf("couriers", d["courierId"]?.ToString());
            var has = d["courierId"] != null && courier != "—";
            all.Add(new DeliveryRow
            {
                Id = d["deliveryId"]!.ToString(),
                Num = "№ " + d["orderId"]!.ToString()[..8].ToUpperInvariant(),
                Customer = NameOf("customers", d["customerId"]?.ToString()),
                Address = d["address"]?.ToString() ?? "",
                Courier = has ? courier : "Не назначен",
                Initials = has ? Initials(courier) : "",
                ChipVisibility = has ? Visibility.Visible : Visibility.Collapsed,
                CourierBrush = has ? R("Text") : R("Faint"),
                Status = st, StatusText = Texts.D(st), StatusFg = Fg(st), StatusBg = Bgc(st),
                Total = Money(d["total"]?.ToString()),
                Created = Dt(d["createdAt"]),
                Cat = Cat(st),
                Node = d
            });
        }
        all = all.OrderByDescending(r => r.Node["createdAt"]!.ToString()).ToList();
        int n(string c) => all.Count(r => r.Cat == c);
        StTotal.Text = all.Count.ToString(); StNew.Text = n("new").ToString(); StActive.Text = n("active").ToString();
        StDone.Text = all.Count(r => r.Status == "Delivered").ToString(); StFail.Text = all.Count(r => r.Status is "Failed" or "Cancelled").ToString();
        BuildStatBar(n("new"), n("active"), all.Count(r => r.Status == "Delivered"), all.Count(r => r.Status is "Failed" or "Cancelled"));
        ((TextBlock)_tabs["all"].Tag).Text = all.Count.ToString(); ((TextBlock)_tabs["new"].Tag).Text = n("new").ToString();
        ((TextBlock)_tabs["active"].Tag).Text = n("active").ToString(); ((TextBlock)_tabs["done"].Tag).Text = n("done").ToString();
        if (_navBadge?.Parent is Border chip) { chip.Visibility = n("new") > 0 ? Visibility.Visible : Visibility.Collapsed; _navBadge.Text = n("new").ToString(); }

        var list = all.Where(r => (_cat == "all" || r.Cat == _cat) &&
            (q.Length == 0 || $"{r.Num} {r.Customer} {r.Address} {r.Courier} {r.StatusText} {r.Node["description"]}".ToLowerInvariant().Contains(q))).ToList();
        OrdersGrid.ItemsSource = new ObservableCollection<DeliveryRow>(list);
        Counter.Text = $"{list.Count} {Plural(list.Count, "заказ", "заказа", "заказов")}";
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sel = list.FirstOrDefault(r => r.Id == _selectedId);
        if (sel != null) OrdersGrid.SelectedItem = sel;
        ShowDetails(); BuildActions(); BuildCouriers();
    }

    private void BuildStatBar(params int[] counts)
    {
        StatBar.ColumnDefinitions.Clear(); StatBar.Children.Clear();
        var colors = new[] { "Created", "InTransit", "Delivered", "Failed" };
        var total = counts.Sum();
        for (int i = 0; i < counts.Length; i++)
        {
            if (total > 0 && counts[i] == 0) continue;
            StatBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total == 0 ? 1 : counts[i], GridUnitType.Star) });
            var b = new Border { Background = total == 0 ? R("Line") : Fg(colors[i]), Opacity = 0.9 };
            System.Windows.Controls.Grid.SetColumn(b, StatBar.ColumnDefinitions.Count - 1); StatBar.Children.Add(b);
            if (total == 0) break;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded && _view == "deliveries") FillDeliveries(); }
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is DeliveryRow r) _selectedId = r.Id;
        ShowDetails(); BuildActions(); BuildCouriers();
    }

    private DeliveryRow? Sel => OrdersGrid.SelectedItem as DeliveryRow;

    // ---------- карточка заказа
    private static readonly string[] Stages = { "Created", "Assigned", "Accepted", "InTransit", "Delivered" };

    private TextBlock T(string text, double size = 13, string brush = "Text", FontWeight? w = null, bool wrap = false, Thickness? m = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = R(brush), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (w != null) t.FontWeight = w.Value; if (m != null) t.Margin = m.Value; return t;
    }

    private StackPanel Section(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        sp.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 10) });
        Details.Children.Add(new Border { BorderBrush = R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = sp });
        return sp;
    }

    private void KV(Panel host, string key, string? value, string brush = "Text")
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return;
        var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(T(key, 12.5, "Muted"));
        var v = T(value, 13, brush, null, true); System.Windows.Controls.Grid.SetColumn(v, 1); g.Children.Add(v);
        host.Children.Add(g);
    }

    private void ShowDetails()
    {
        Details.Children.Clear();
        if (Sel is not { } r)
        {
            var empty = new StackPanel { Margin = new Thickness(24, 70, 24, 24), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new Border { Width = 38, Height = 38, BorderBrush = R("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Path { Data = Geometry.Parse("M10,13 L19,9 L28,13 L28,25 L19,29 L10,25 Z M10,13 L19,17 L28,13 M19,17 L19,29"), Stroke = R("Faint"), StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None } });
            empty.Children.Add(T("Заказ не выбран", 14, "Text", FontWeights.Medium, false, new Thickness(0, 0, 0, 4)));
            var h = T("Выберите заказ в таблице, чтобы увидеть состав, курьера и историю статусов", 12.5, "Muted", null, true); h.TextAlignment = TextAlignment.Center; empty.Children.Add(h);
            Details.Children.Add(empty); return;
        }
        var d = r.Node;
        var hist = d["history"]?.AsArray() ?? new JsonArray();

        // шапка: номер, статус, адрес
        var head = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        var top = new DockPanel();
        var pill = new Border { Background = r.StatusBg, CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Right, Child = T(r.StatusText, 11.5, "Text", FontWeights.Medium) };
        ((TextBlock)pill.Child).Foreground = r.StatusFg; DockPanel.SetDock(pill, Dock.Right);
        top.Children.Add(pill);
        var num = new TextBlock { Text = "ЗАКАЗ  " + r.Num, Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(num); head.Children.Add(top);
        head.Children.Add(T(r.Address, 17, "Text", FontWeights.SemiBold, true, new Thickness(0, 10, 0, 4)));
        head.Children.Add(T($"{r.Customer}  ·  {r.Total}", 12.5, "Muted"));
        head.Children.Add(StageBar(r.Status, hist));
        Details.Children.Add(new Border { Background = R("Raised"), Child = head });

        // клиент
        var cust = Find("customers", d["customerId"]?.ToString());
        var s1 = Section("КЛИЕНТ");
        KV(s1, "Имя", r.Customer); KV(s1, "Телефон", cust?["phone"]?.ToString()); KV(s1, "Адрес", r.Address);
        KV(s1, "Комментарий", d["comment"]?.ToString());

        // курьер
        var cour = Find("couriers", d["courierId"]?.ToString());
        var s2 = Section("КУРЬЕР");
        if (cour is null) s2.Children.Add(T("Курьер не назначен", 13, "Faint"));
        else
        {
            KV(s2, "Имя", cour["name"]?.ToString()); KV(s2, "Телефон", cour["phone"]?.ToString());
            KV(s2, "Состояние", Texts.C(cour["state"]?.ToString()));
        }

        // состав
        var s3 = Section("СОСТАВ ЗАКАЗА");
        foreach (var i in d["items"]?.AsArray() ?? new JsonArray())
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(T($"{i!["name"]}  ×{i["quantity"]}", 13, "Text", null, true));
            var pr = T(Money((decimal.Parse(i["price"]!.ToString(), CultureInfo.InvariantCulture) * int.Parse(i["quantity"]!.ToString())).ToString(CultureInfo.InvariantCulture)), 12.5, "Muted"); System.Windows.Controls.Grid.SetColumn(pr, 1); g.Children.Add(pr);
            s3.Children.Add(g);
        }
        var tot = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        tot.ColumnDefinitions.Add(new ColumnDefinition()); tot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tot.Children.Add(T("Итого", 13, "Muted")); var tv = T(r.Total, 14, "Text", FontWeights.SemiBold); System.Windows.Controls.Grid.SetColumn(tv, 1); tot.Children.Add(tv);
        s3.Children.Add(tot);

        // время
        var s4 = Section("ВРЕМЯ");
        KV(s4, "Статус заказа", Texts.O(d["orderStatus"]?.ToString()), d["orderStatus"]?.ToString() is "DeliveryFailed" or "Cancelled" ? "Danger" : "Text");
        KV(s4, "Создан", r.Created); KV(s4, "Начата", Dt(d["startedAt"])); KV(s4, "Завершена", Dt(d["finishedAt"]));
        if (d["confirmedAt"] != null) KV(s4, "Получение", Dt(d["confirmedAt"]));
        KV(s4, "Причина сбоя", d["failureReason"]?.ToString(), "Danger");

        // история
        var s5 = Section("ИСТОРИЯ СТАТУСОВ");
        var items = hist.Reverse().ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var h = items[i]!; var st = h["status"]!.ToString();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var line = new Rectangle { Width = 1, Fill = R("Line"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Visibility = i == items.Count - 1 ? Visibility.Collapsed : Visibility.Visible };
            var dot = new Ellipse { Width = 7, Height = 7, Fill = i == 0 ? Fg(st) : R("LineStrong"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
            g.Children.Add(line); g.Children.Add(dot);
            var tx = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            tx.Children.Add(T(Texts.D(st), 13, i == 0 ? "Text" : "Muted", i == 0 ? FontWeights.Medium : FontWeights.Normal));
            tx.Children.Add(T(Dt(h["at"]), 11.5, "Faint"));
            if (!string.IsNullOrWhiteSpace(h["comment"]?.ToString())) tx.Children.Add(T(h["comment"]!.ToString(), 12, "Muted", null, true, new Thickness(0, 2, 0, 0)));
            System.Windows.Controls.Grid.SetColumn(tx, 1); g.Children.Add(tx);
            s5.Children.Add(g);
        }

        // журнал изменений заказа: состав, адрес, комментарий (кто и когда изменил)
        var changes = d["changes"]?.AsArray() ?? new JsonArray();
        if (changes.Count > 0)
        {
            var s6 = Section("ИЗМЕНЕНИЯ ЗАКАЗА");
            foreach (var ch in changes.Reverse())
            {
                var field = ch!["field"]?.ToString() switch { "Items" => "Состав", "Address" => "Адрес", "Comment" => "Комментарий", var f => f ?? "" };
                var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                box.Children.Add(T(field, 13, "Text", FontWeights.Medium));
                box.Children.Add(T($"{Dt(ch["at"])}  ·  {ch["actorName"]?.ToString() ?? "—"}", 11.5, "Faint"));
                box.Children.Add(T("было: " + (string.IsNullOrEmpty(ch["oldValue"]?.ToString()) ? "—" : ch["oldValue"]!.ToString()), 12, "Muted", null, true, new Thickness(0, 2, 0, 0)));
                box.Children.Add(T("стало: " + (string.IsNullOrEmpty(ch["newValue"]?.ToString()) ? "—" : ch["newValue"]!.ToString()), 12, "Text", null, true));
                s6.Children.Add(box);
            }
        }
    }

    private UIElement StageBar(string status, JsonArray hist)
    {
        var failed = status is "Failed" or "Cancelled";
        int idx = Array.IndexOf(Stages, status);
        if (failed) idx = Math.Max(0, hist.Select(h => Array.IndexOf(Stages, h!["status"]!.ToString())).DefaultIfEmpty(0).Max());
        var wrap = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var bar = new UniformGrid { Rows = 1, Columns = Stages.Length };
        for (int i = 0; i < Stages.Length; i++)
        {
            Brush c = i <= idx ? R("Accent") : R("LineStrong");
            if (failed && i == idx) c = Fg(status);
            bar.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = c, Margin = new Thickness(0, 0, i == Stages.Length - 1 ? 0 : 3, 0) });
        }
        wrap.Children.Add(bar);
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = T(failed ? "Не завершена" : $"Этап {idx + 1} из {Stages.Length}", 11.5, "Faint"); DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(T(failed ? Texts.D(status) : Texts.D(Stages[idx]), 12.5, "Text", FontWeights.Medium));
        wrap.Children.Add(row);
        return wrap;
    }

    // ---------- панель курьеров (оператор)
    private void BuildCouriers()
    {
        var isOp = Api.Role == "operator";
        CouriersPanel.Visibility = isOp ? Visibility.Visible : Visibility.Collapsed;
        if (!isOp) return;
        Couriers.Children.Clear();
        var list = Arr("couriers").Select(c => c!).ToList();
        var free = list.Count(c => c["state"]!.ToString() == "Available");
        var load = Arr("deliveries").Where(d => Cat(d!["status"]!.ToString()) == "active" && d["courierId"] != null).GroupBy(d => d!["courierId"]!.ToString()).ToDictionary(g => g.Key, g => g.Count());

        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var dp = new DockPanel();
        var cnt = T($"{free} из {list.Count} свободно", 11.5, "Muted"); DockPanel.SetDock(cnt, Dock.Right); dp.Children.Add(cnt);
        dp.Children.Add(new TextBlock { Text = "КУРЬЕРЫ", Style = (Style)FindResource("Caption") });
        head.Children.Add(dp);
        var seg = new UniformGrid { Rows = 1, Columns = Math.Max(1, list.Count), Margin = new Thickness(0, 9, 0, 0) };
        foreach (var c in list) seg.Children.Add(new Border { Height = 3, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(1.5), Background = Fg(c["state"]!.ToString()) });
        head.Children.Add(seg);
        Couriers.Children.Add(head);

        var canAssign = Sel?.Status == "Created";
        foreach (var (title, pred) in new (string, Func<string, bool>)[] { ("СВОБОДНЫ", s => s == "Available"), ("В РАБОТЕ", s => s == "Busy"), ("НЕ НА ЛИНИИ", s => s is "Offline" or "Blocked") })
        {
            var group = list.Where(c => pred(c["state"]!.ToString())).ToList();
            if (group.Count == 0) continue;
            Couriers.Children.Add(new Border { BorderBrush = R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = new TextBlock { Text = title, Style = (Style)FindResource("Caption"), Margin = new Thickness(16, 10, 16, 4) } });
            foreach (var c in group)
            {
                var st = c["state"]!.ToString(); var id = c["id"]!.ToString();
                var g = new Grid { Margin = new Thickness(16, 5, 16, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var av = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(4), Background = Bgc(st), Child = new TextBlock { Text = Initials(c["name"]!.ToString()), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Fg(st), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                g.Children.Add(av);
                var n = load.GetValueOrDefault(id);
                var tx = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                tx.Children.Add(T(c["name"]!.ToString(), 13, "Text", FontWeights.Medium));
                tx.Children.Add(T(n > 0 ? $"{Texts.C(st)} · {n} {Plural(n, "заказ", "заказа", "заказов")}" : Texts.C(st), 11.5, "Muted"));
                var ph = c["phone"]?.ToString(); if (!string.IsNullOrEmpty(ph)) tx.Children.Add(T(ph, 11.5, "Faint"));
                var curD = Arr("deliveries").FirstOrDefault(d => d!["courierId"]?.ToString() == id && Cat(d["status"]!.ToString()) == "active");
                if (curD != null) { var a = T("→ " + (curD["address"]?.ToString() ?? ""), 11.5, "Muted"); a.TextTrimming = TextTrimming.CharacterEllipsis; tx.Children.Add(a); }
                System.Windows.Controls.Grid.SetColumn(tx, 1); g.Children.Add(tx);
                if (st == "Available" && canAssign)
                {
                    var b = new Button { Content = "Назначить", Style = (Style)FindResource("Compact"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                    var did = Sel!.Id;
                    b.Click += async (_, _) => await Safe(async () => { await Api.PostAsync($"api/deliveries/{did}/assign", new { courierId = id }); await Reload(); });
                    System.Windows.Controls.Grid.SetColumn(b, 2); g.Children.Add(b);
                }
                Couriers.Children.Add(g);
            }
        }
        Couriers.Children.Add(new Border { Height = 6 });
    }

    // ---------- кнопки действий по роли
    private void Btn(Panel host, string text, Func<Task> act, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        if (style != null) b.Style = (Style)FindResource(style == "Danger" ? "DangerBtn" : style);
        b.Click += async (_, _) => await Safe(async () => { await act(); await Reload(); });
        host.Children.Add(b);
    }

    private void BuildActions()
    {
        Actions.Children.Clear(); OrderActions.Children.Clear();
        var role = Api.Role; var r = Sel; var st = r?.Status;
        if (role == "operator" && r != null)
        {
            if (st == "Created") Btn(OrderActions, "Назначить курьера", AssignAsync, "Primary");
            var next = Texts.Next.GetValueOrDefault(st ?? "")?.Where(s => s != "Assigned").ToArray() ?? Array.Empty<string>();
            if (next.Length > 0) Btn(OrderActions, "Сменить статус", () => OperatorStatusAsync(next));
            if (st == "Delivered" && r.Node["confirmedAt"] == null) Btn(OrderActions, "Подтвердить получение", () => Api.PostAsync($"api/deliveries/{r.Id}/confirm"), "Primary");
        }
        else if (role == "client")
        {
            Btn(Actions, "Новый заказ", () => OrderAsync(null), "Primary");
            if (r != null && st is "Created" or "Assigned") { Btn(OrderActions, "Изменить", () => OrderAsync(r.Node)); Btn(OrderActions, "Отменить заказ", () => SetStatus(r.Id, "Cancelled", null), "Danger"); }
            if (r != null && st == "Delivered" && r.Node["confirmedAt"] == null) Btn(OrderActions, "Подтвердить получение", () => Api.PostAsync($"api/deliveries/{r.Id}/confirm"), "Primary");
        }
        else if (role == "courier")
        {
            var me = Arr("couriers").FirstOrDefault();
            var cs = me?["state"]?.ToString();
            if (r != null)
            {
                if (st == "Assigned") Btn(OrderActions, "Принять", () => SetStatus(r.Id, "Accepted", null), "Primary");
                if (st == "Accepted") Btn(OrderActions, "В пути", () => SetStatus(r.Id, "InTransit", null), "Primary");
                if (st == "InTransit") Btn(OrderActions, "Доставлено", () => SetStatus(r.Id, "Delivered", null), "Primary");
                if (st is "Assigned" or "Accepted" or "InTransit") Btn(OrderActions, "Не удалось", () => FailAsync(r.Id), "Danger");
            }
            if (cs != null)
            {
                var chip = new Border { Background = Bgc(cs), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = Texts.C(cs), Foreground = Fg(cs), FontSize = 11.5, FontWeight = FontWeights.Medium } };
                Actions.Children.Add(chip);
            }
            if (cs is "Available" or "Offline")
                Btn(Actions, cs == "Available" ? "Уйти с линии" : "Выйти на линию", () => Api.PatchAsync($"api/couriers/{Api.RefId}/state", new { state = cs == "Available" ? "Offline" : "Available" }), cs == "Offline" ? "Primary" : null);
        }
        ActionBar.Visibility = OrderActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            MsgDialog.Info(this, "Заказ оформлен", r?["message"]?.ToString() ?? "Заказ создан");
        }
        else await Api.PutAsync($"api/orders/{existing["orderId"]}", dlg.Body);
    }

    // ---------- справочники оператора
    private void FillDirectory()
    {
        DirGrid.Columns.Clear(); DirActions.Children.Clear();
        var rows = new List<DirRow>();
        void Text(string h, string path, double w = 0) => DirGrid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(path), Width = w > 0 ? new DataGridLength(w) : new DataGridLength(1, DataGridLengthUnitType.Star) });
        var deliveries = Arr("deliveries");
        if (_view == "customers")
        {
            DirTitle.Text = "Клиенты"; Crumb2.Text = "Клиенты";
            Text("ИМЯ", "A"); Text("ТЕЛЕФОН", "B", 220); Text("ЗАКАЗОВ", "D", 120);
            rows = Arr("customers").Select(c => new DirRow { Id = c!["id"]!.ToString(), A = c["name"]!.ToString(), B = c["phone"]?.ToString() ?? "", D = deliveries.Count(d => d!["customerId"]?.ToString() == c["id"]!.ToString()).ToString() }).ToList();
            DirSub.Text = $"{rows.Count} {Plural(rows.Count, "клиент", "клиента", "клиентов")} · справочник клиентов";
        }
        else if (_view == "couriers")
        {
            DirTitle.Text = "Курьеры"; Crumb2.Text = "Курьеры";
            Text("ИМЯ", "A"); Text("ТЕЛЕФОН", "B", 200);
            DirGrid.Columns.Add(new DataGridTemplateColumn { Header = "СОСТОЯНИЕ", Width = 170, CellTemplate = (DataTemplate)FindResource("DirPill") });
            Text("В РАБОТЕ", "D", 90); Text("ТЕКУЩИЙ АДРЕС", "H"); Text("ДОСТАВЛЕНО", "E", 110); Text("НЕ ВЫПОЛНЕНО", "F", 120); Text("ВСЕГО", "G", 80);
            rows = Arr("couriers").Select(c =>
            {
                var s = c!["state"]!.ToString();
                var mine = deliveries.Where(d => d!["courierId"]?.ToString() == c["id"]!.ToString()).ToList();
                var cur = mine.FirstOrDefault(d => Cat(d!["status"]!.ToString()) == "active");
                return new DirRow { Id = c["id"]!.ToString(), A = c["name"]!.ToString(), B = c["phone"]?.ToString() ?? "", C = Texts.C(s), State = s, StatusFg = Fg(s), StatusBg = Bgc(s),
                    D = mine.Count(d => Cat(d!["status"]!.ToString()) == "active").ToString(), H = cur?["address"]?.ToString() ?? "—",
                    E = mine.Count(d => d!["status"]!.ToString() == "Delivered").ToString(), F = mine.Count(d => d!["status"]!.ToString() is "Failed" or "Cancelled").ToString(), G = mine.Count.ToString() };
            }).ToList();
            DirSub.Text = $"{rows.Count} {Plural(rows.Count, "курьер", "курьера", "курьеров")} · управление состоянием и составом";
        }
        else
        {
            DirTitle.Text = "Товары"; Crumb2.Text = "Товары";
            Text("НАЗВАНИЕ", "A"); Text("ЦЕНА, ₽", "B", 160);
            rows = Arr("products").Select(p => new DirRow { Id = p!["id"]!.ToString(), A = p["name"]!.ToString(), B = p["price"]!.ToString() }).ToList();
            DirSub.Text = $"{rows.Count} {Plural(rows.Count, "позиция", "позиции", "позиций")} · каталог для оформления заказов";
        }
        DirGrid.ItemsSource = rows;
        if (_view == "couriers") Btn(DirActions, "Состояние", CourierStateAsync);
        Btn(DirActions, "Изменить", EditEntryAsync);
        Btn(DirActions, "Удалить", DeleteEntryAsync, "Danger");
        Btn(DirActions, "Добавить", AddEntryAsync, "Primary");
    }

    private void DirGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DirGrid.SelectedItem is DirRow) _ = Safe(async () => { await EditEntryAsync(); await Reload(); });
    }

    private async Task AddEntryAsync()
    {
        var product = _view == "products";
        var dlg = new PersonDialog(this, _view == "customers" ? "Новый клиент" : _view == "couriers" ? "Новый курьер" : "Новый товар", product);
        if (dlg.ShowDialog() != true) return;
        JsonNode? r;
        if (product)
        {
            if (!decimal.TryParse(dlg.V2.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) { MsgDialog.Info(this, "Укажите цену числом"); return; }
            r = await Api.PostAsync("api/products", new { name = dlg.V1, price });
        }
        else
        {
            r = await Api.PostAsync("api/" + _view, new { name = dlg.V1, phone = dlg.V2, login = dlg.V3.Length > 0 ? dlg.V3 : null, password = dlg.V4.Length > 0 ? dlg.V4 : null });
            var login = r?["login"]?.ToString() ?? r?["credentials"]?["login"]?.ToString();
            var pass = r?["password"]?.ToString() ?? r?["credentials"]?["password"]?.ToString();
            if (login != null) MsgDialog.Info(this, "Учётная запись создана", $"Логин: {login}\nПароль: {pass}");
        }
    }

    private async Task EditEntryAsync()
    {
        if (DirGrid.SelectedItem is not DirRow r) { MsgDialog.Info(this, "Выберите запись в таблице"); return; }
        var product = _view == "products";
        var dlg = new PersonDialog(this, _view == "customers" ? "Изменить клиента" : _view == "couriers" ? "Изменить курьера" : "Изменить товар", product, r.A, r.B, true);
        if (dlg.ShowDialog() != true) return;
        if (product)
        {
            if (!decimal.TryParse(dlg.V2.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) { MsgDialog.Info(this, "Укажите цену числом"); return; }
            await Api.PutAsync($"api/products/{r.Id}", new { name = dlg.V1, price });
        }
        else await Api.PutAsync($"api/{_view}/{r.Id}", new { name = dlg.V1, phone = dlg.V2 });
    }

    private async Task DeleteEntryAsync()
    {
        if (DirGrid.SelectedItem is not DirRow r) { MsgDialog.Info(this, "Выберите запись в таблице"); return; }
        if (!MsgDialog.Confirm(this, $"Удалить «{r.A}»?", "Действие нельзя отменить.", "Удалить")) return;
        await Api.DeleteAsync($"api/{_view}/{r.Id}");
    }

    private async Task CourierStateAsync()
    {
        if (DirGrid.SelectedItem is not DirRow r) { MsgDialog.Info(this, "Выберите курьера в таблице"); return; }
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

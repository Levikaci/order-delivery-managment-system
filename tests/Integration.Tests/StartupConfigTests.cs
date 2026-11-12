using ApiHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Integration.Tests;

/// <summary>Разделение окружений: рабочий запуск без стандартных паролей и демо-пользователей.</summary>
public class StartupConfigTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ApiHost";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private const string StrongCs = "Host=db;Port=5432;Database=delivery;Username=delivery;Password=Vq7!kR2#pL9x";
    private static readonly IHostEnvironment Prod = new Env(Environments.Production);
    private static readonly IHostEnvironment Dev = new Env(Environments.Development);

    private static IConfiguration Cfg(string? cs, string? demo = null, string? operatorPassword = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = cs, ["Seed:Demo"] = demo, ["Seed:OperatorPassword"] = operatorPassword
        }).Build();

    [Fact]
    public void Production_WithStrongSettings_IsValid() =>
        Assert.Empty(StartupChecks.Validate(Cfg(StrongCs, "false", "Op3rator-Strong"), Prod));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Production_WithoutConnectionString_IsRejected(string? cs) =>
        Assert.Contains(StartupChecks.Validate(Cfg(cs), Prod), p => p.Contains("ConnectionStrings:Default"));

    [Theory]
    [InlineData("Host=db;Username=delivery;Password=delivery")]
    [InlineData("Host=db;Username=postgres;Password=postgres")]
    [InlineData("Host=db;Username=delivery;Password=short")]
    [InlineData("Host=db;Username=delivery;Password=aaaaaaaaaaaaaaaa")]
    [InlineData("Host=db;Username=delivery")]
    public void Production_WithDefaultOrWeakDbPassword_IsRejected(string cs) =>
        Assert.Contains(StartupChecks.Validate(Cfg(cs), Prod), p => p.Contains("PostgreSQL"));

    [Fact]
    public void Production_WithDemoSeed_IsRejected() =>
        Assert.Contains(StartupChecks.Validate(Cfg(StrongCs, "true"), Prod), p => p.Contains("Seed:Demo"));

    [Theory]
    [InlineData("operator")]
    [InlineData("1234567")]
    [InlineData("changeme")]
    public void Production_WithWeakOperatorPassword_IsRejected(string pwd) =>
        Assert.Contains(StartupChecks.Validate(Cfg(StrongCs, "false", pwd), Prod), p => p.Contains("Seed:OperatorPassword"));

    [Fact]
    public void Production_ReportsAllProblemsAtOnce()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StartupChecks.EnsureValid(Cfg("Host=db;Username=delivery;Password=delivery", "true", "operator"), Prod));
        Assert.Contains("PostgreSQL", ex.Message);
        Assert.Contains("Seed:Demo", ex.Message);
        Assert.Contains("Seed:OperatorPassword", ex.Message);
    }

    [Fact]
    public void Development_AllowsLocalDefaults() =>
        Assert.Empty(StartupChecks.Validate(Cfg("Host=localhost;Username=delivery;Password=delivery", "true"), Dev));

    [Fact]
    public void Development_StillRequiresConnectionString() =>
        Assert.NotEmpty(StartupChecks.Validate(Cfg(null), Dev));

    [Fact]
    public void Store_WithoutConnectionString_DoesNotFallBackToDefaultPassword() =>
        Assert.Throws<InvalidOperationException>(() => new Store(Cfg(null), NullLogger<Store>.Instance));

    [Fact]
    public void Store_InProduction_WithoutSeedSetting_DoesNotCreateDemoUsers()
    {
        using var db = new TestDb();
        using var s = new Store(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.ConnectionString, ["Seed:OperatorPassword"] = "Op3rator-Strong"
        }).Build(), NullLogger<Store>.Instance, Prod);
        Assert.Empty(s.Customers());
        Assert.Empty(s.Couriers());
        Assert.Null(s.Authenticate("ivan", "ivan"));
        Assert.Null(s.Authenticate("operator", "operator"));
        Assert.NotNull(s.Authenticate("operator", "Op3rator-Strong"));
    }

    [Fact]
    public void Api_InProduction_WithTestDefaults_DoesNotStart()
    {
        using var f = new ApiFactory();
        using var prod = f.WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        Assert.Contains("Небезопасная или неполная конфигурация", ex.ToString());
        Assert.Equal(0, f.Db.Scalar<long>("SELECT COUNT(*) FROM pg_tables WHERE tablename='users'")); // до базы дело не дошло
    }

    [Fact]
    public async Task Api_InProduction_WithStrongSettings_Starts_WithoutDemoUsers()
    {
        using var db = new TestDb();
        db.Sql("CREATE ROLE app_user LOGIN PASSWORD 'Vq7!kR2#pL9x-app'; GRANT ALL ON SCHEMA public TO app_user;");
        try
        {
            var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = "app_user", Password = "Vq7!kR2#pL9x-app" }.ToString();
            using var f = new ApiFactory(db).WithWebHostBuilder(b => b.UseEnvironment(Environments.Production)
                .UseSetting("ConnectionStrings:Default", cs).UseSetting("Seed:OperatorPassword", "Op3rator-Strong"));
            var c = f.CreateClient();
            var demo = await c.PostAsync("/api/auth/login", new StringContent("""{"login":"ivan","password":"ivan"}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, demo.StatusCode);
            var op = await c.PostAsync("/api/auth/login", new StringContent("""{"login":"operator","password":"Op3rator-Strong"}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.OK, op.StatusCode);
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            db.Sql("REASSIGN OWNED BY app_user TO CURRENT_USER; DROP OWNED BY app_user;");
            using var admin = new Npgsql.NpgsqlConnection(TestDb.AdminConnectionString);
            admin.Open();
            new Npgsql.NpgsqlCommand("DROP ROLE IF EXISTS app_user", admin).ExecuteNonQuery();
        }
    }

    [Fact]
    public void ProductionCompose_DoesNotPublishDatabase_AndHasNoDefaultPasswords()
    {
        var root = FindRepoRoot();
        var prod = File.ReadAllText(Path.Combine(root, "docker-compose.prod.yml"));
        var db = prod[prod.IndexOf("  db:", StringComparison.Ordinal)..prod.IndexOf("  app:", StringComparison.Ordinal)];
        Assert.DoesNotContain("ports:", db);
        Assert.DoesNotContain("5432:5432", prod);
        Assert.DoesNotContain("POSTGRES_PASSWORD:-", prod);
        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", prod);

        var dev = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        Assert.Contains("127.0.0.1:${DB_PORT:-5432}:5432", dev);   // локально — только на loopback
    }

    [Fact]
    public void SharedAppsettings_ContainNoPasswords()
    {
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "ApiHost", "appsettings.json"));
        Assert.DoesNotContain("Password=", json, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "OrderDeliveryManagementSystem.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Корень репозитория не найден");
    }
}

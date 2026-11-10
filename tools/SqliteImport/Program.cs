using Npgsql;
using SqliteImport;

if (args.Length != 2)
{
    Console.Error.WriteLine("Использование: dotnet run --project tools/SqliteImport -- <путь к delivery.db> \"<строка подключения PostgreSQL>\"");
    return 2;
}
try
{
    using var ds = NpgsqlDataSource.Create(args[1]);
    foreach (var (table, n) in SqliteImporter.Run(args[0], ds)) Console.WriteLine($"{table,-20} {n}");
    Console.WriteLine("Готово. Исходный файл SQLite не изменён.");
    return 0;
}
catch (Exception ex) when (ex is ImportException or ApiHost.Db.MigrationException)
{
    Console.Error.WriteLine("Ошибка: " + ex.Message);
    return 1;
}

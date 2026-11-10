using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NurseShift.Core.Interfaces;

namespace NurseShift.Infrastructure.Persistence;

public interface INurseShiftDbContextFactory
{
    Task<NurseShiftDbContext> CreateAsync(CancellationToken cancellationToken);
}

public sealed class NurseShiftDbContextFactory : INurseShiftDbContextFactory
{
    public const string DatabaseKeyName = "database.encryption_key";
    private readonly string _databasePath;
    private readonly ISecretStore _secretStore;

    public NurseShiftDbContextFactory(string databasePath, ISecretStore secretStore)
    {
        _databasePath = databasePath;
        _secretStore = secretStore;
        SQLitePCL.Batteries_V2.Init();
    }

    public async Task<NurseShiftDbContext> CreateAsync(CancellationToken cancellationToken)
    {
        var key = await _secretStore.GetAsync(DatabaseKeyName, cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await _secretStore.SetAsync(DatabaseKeyName, key, cancellationToken);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Password = key,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<NurseShiftDbContext>().UseSqlite(connectionString).Options;
        var context = new NurseShiftDbContext(options);
        context.Database.SetCommandTimeout(TimeSpan.FromSeconds(30));
        return context;
    }
}

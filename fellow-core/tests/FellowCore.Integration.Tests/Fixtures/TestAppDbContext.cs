using FellowCore.Domain.Entities;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FellowCore.Integration.Tests.Fixtures;

public class TestAppDbContext : AppDbContext
{
    public TestAppDbContext(DbContextOptions<TestAppDbContext> options)
        : base(ConvertOptions(options))
    {
    }

    private static DbContextOptions<AppDbContext> ConvertOptions(DbContextOptions<TestAppDbContext> options)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        foreach (var extension in options.Extensions)
        {
            ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(extension);
        }
        return builder.Options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // SQLite doesn't support RowVersion the same way as Npgsql
        modelBuilder.Entity<Transaction>()
            .Property(t => t.RowVersion)
            .IsConcurrencyToken(false)
            .ValueGeneratedNever();

        modelBuilder.Entity<LedgerAccount>()
            .Property(a => a.RowVersion)
            .IsConcurrencyToken(false)
            .ValueGeneratedNever();

        modelBuilder.Entity<PaymentIntent>()
            .Property(p => p.RowVersion)
            .IsConcurrencyToken(false)
            .ValueGeneratedNever();
    }
}

using System.Text.Json;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FellowCore.Infrastructure.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<Transaction> Transactions { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<JsonDocument>()
            .HaveConversion<JsonDocumentConverter, JsonDocumentComparer>()
            .HaveMaxLength(8192);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Transaction>().Ignore(e => e.DomainEvents);
        modelBuilder.Entity<Customer>().Ignore(e => e.DomainEvents);
        modelBuilder.Entity<User>().Ignore(e => e.DomainEvents);

        modelBuilder.Entity<Customer>(customer =>
        {
            customer.HasIndex(c => new { c.TenantId, c.Email }).IsUnique();
        });

        modelBuilder.Entity<Transaction>(transaction =>
        {
            transaction.HasIndex(t => new { t.TenantId, t.IdempotencyKey }).IsUnique();
            transaction.HasIndex(t => t.SellerId);
            transaction.HasIndex(t => t.CustomerId);
            transaction.HasIndex(t => new { t.TenantId, t.CreatedAt });
            transaction.Property(t => t.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Transaction>()
            .HasOne(t => t.Customer)
            .WithMany(c => c.Transactions)
            .HasForeignKey(t => t.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class JsonDocumentConverter : ValueConverter<JsonDocument?, string?>
{
    public JsonDocumentConverter() : base(
        v => v != null ? v.RootElement.GetRawText() : null,
        v => v != null ? JsonDocument.Parse(v, default) : null)
    { }
}

public class JsonDocumentComparer : ValueComparer<JsonDocument?>
{
    public JsonDocumentComparer() : base(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.RootElement.GetRawText() == b.RootElement.GetRawText()),
        v => v != null ? v.RootElement.GetRawText().GetHashCode() : 0,
        v => v != null ? JsonDocument.Parse(v.RootElement.GetRawText(), default) : null)
    { }
}
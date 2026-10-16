using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MiniMes.Core.Models;

namespace MiniMes.Data;

public class MiniMesDbContext : DbContext
{
    public MiniMesDbContext(DbContextOptions<MiniMesDbContext> options) : base(options) { }

    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<BatchEvent> BatchEvents => Set<BatchEvent>();
    public DbSet<BatchXmlMessage> XmlMessages => Set<BatchXmlMessage>();
    public DbSet<Tank> Tanks => Set<Tank>();
    public DbSet<TankMovement> TankMovements => Set<TankMovement>();

    // SQLite doesn't remember that a DateTime is UTC; mark every date as UTC when reading it back,
    // so the API returns "...Z" and clients can't mistake it for local time.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    private sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter() : base(
            v => v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recipe>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).IsRequired().HasMaxLength(100);
            e.HasIndex(r => r.Name).IsUnique();

            // Recipe exposes a read-only list backed by a private field; tell EF to use the field.
            e.HasMany(r => r.Ingredients).WithOne().HasForeignKey(i => i.RecipeId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(r => r.Ingredients).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<RecipeIngredient>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.IngredientName).IsRequired().HasMaxLength(100);
            e.Property(i => i.Container).HasConversion<string>().HasMaxLength(1);
            e.Property(i => i.Percentage).HasPrecision(5, 2);
        });

        modelBuilder.Entity<Batch>(e =>
        {
            e.HasKey(b => b.BatchId);
            e.Property(b => b.BatchId).HasMaxLength(30);
            e.Property(b => b.PlannedQuantityKg).HasPrecision(10, 3);
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.ErrorMessage).HasMaxLength(500);
            e.HasOne(b => b.Recipe).WithMany().HasForeignKey(b => b.RecipeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(b => b.CreatedAtUtc);
        });

        modelBuilder.Entity<BatchXmlMessage>(e =>
        {
            e.ToTable("BatchXmlMessages");
            e.HasKey(m => m.Id);
            e.Property(m => m.BatchId).IsRequired().HasMaxLength(30);
            e.Property(m => m.FileName).IsRequired().HasMaxLength(100);
            e.Property(m => m.Xml).IsRequired();   // the full document, no length limit
            e.Property(m => m.Result).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Detail).HasMaxLength(500);
            e.HasOne<Batch>().WithMany().HasForeignKey(m => m.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.BatchId, m.CreatedAtUtc });
        });

        modelBuilder.Entity<Tank>(e =>
        {
            e.HasKey(t => t.Container);   // one tank per container
            e.Property(t => t.Container).HasConversion<string>().HasMaxLength(1);
            e.Property(t => t.MaterialName).IsRequired().HasMaxLength(100);
            e.Property(t => t.CapacityKg).HasPrecision(10, 3);
            e.Property(t => t.LevelKg).HasPrecision(10, 3);
            e.Ignore(t => t.IsLow);
        });

        modelBuilder.Entity<TankMovement>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Container).HasConversion<string>().HasMaxLength(1);
            e.Property(m => m.DeltaKg).HasPrecision(10, 3);
            e.Property(m => m.LevelAfterKg).HasPrecision(10, 3);
            e.Property(m => m.Reason).IsRequired().HasMaxLength(200);
            e.Property(m => m.BatchId).HasMaxLength(30);
            e.HasOne<Batch>().WithMany().HasForeignKey(m => m.BatchId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => m.TimestampUtc);
        });

        modelBuilder.Entity<BatchEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.Level).HasConversion<string>().HasMaxLength(10);
            e.Property(ev => ev.Message).IsRequired().HasMaxLength(500);
            e.HasOne<Batch>().WithMany().HasForeignKey(ev => ev.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(ev => new { ev.BatchId, ev.TimestampUtc });
        });
    }
}

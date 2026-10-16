using Microsoft.EntityFrameworkCore;
using MiniMes.Core.Models;

namespace MiniMes.Data;

public class MiniMesDbContext : DbContext
{
    public MiniMesDbContext(DbContextOptions<MiniMesDbContext> options) : base(options) { }

    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<BatchEvent> BatchEvents => Set<BatchEvent>();

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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MiniMes.Data;

/// <summary>Lets the `dotnet ef` tool create the DbContext without starting the web app.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MiniMesDbContext>
{
    public MiniMesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MiniMesDbContext>()
            .UseSqlite("Data Source=minimes.db")
            .Options;
        return new MiniMesDbContext(options);
    }
}

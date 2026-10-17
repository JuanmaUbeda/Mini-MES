using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Counters",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Value = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Counters", x => x.Name);
                });

            // Continue after the batches that already exist, so old and new numbers never collide.
            migrationBuilder.Sql("INSERT INTO Counters (Name, Value) SELECT 'Batch', COUNT(*) FROM Batches;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Counters");
        }
    }
}

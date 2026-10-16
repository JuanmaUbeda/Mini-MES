using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MiniMes.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTankInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TankMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Container = table.Column<string>(type: "TEXT", maxLength: 1, nullable: false),
                    DeltaKg = table.Column<decimal>(type: "TEXT", precision: 10, scale: 3, nullable: false),
                    LevelAfterKg = table.Column<decimal>(type: "TEXT", precision: 10, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BatchId = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankMovements_Batches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "Batches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tanks",
                columns: table => new
                {
                    Container = table.Column<string>(type: "TEXT", maxLength: 1, nullable: false),
                    MaterialName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CapacityKg = table.Column<decimal>(type: "TEXT", precision: 10, scale: 3, nullable: false),
                    LevelKg = table.Column<decimal>(type: "TEXT", precision: 10, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tanks", x => x.Container);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TankMovements_BatchId",
                table: "TankMovements",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_TankMovements_TimestampUtc",
                table: "TankMovements",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TankMovements");

            migrationBuilder.DropTable(
                name: "Tanks");
        }
    }
}

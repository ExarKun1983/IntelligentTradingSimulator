using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Mig4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObservationResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObservationDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MomentumPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    VolumeTrendPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    VolatilityPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    IsAboveSma5 = table.Column<bool>(type: "bit", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    RankPosition = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_ObservationDate",
                table: "ObservationResults",
                column: "ObservationDate");

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_ObservationDate_RankPosition",
                table: "ObservationResults",
                columns: new[] { "ObservationDate", "RankPosition" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_Symbol",
                table: "ObservationResults",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObservationResults");
        }
    }
}

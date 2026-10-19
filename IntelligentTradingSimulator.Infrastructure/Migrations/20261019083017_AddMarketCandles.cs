using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketCandles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketCandles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: false),
                    High = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: false),
                    Low = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: false),
                    Close = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: false),
                    Volume = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PriceAdjustmentMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketCandles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketDataCoverage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PriceAdjustmentMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    From = table.Column<DateTime>(type: "date", nullable: false),
                    To = table.Column<DateTime>(type: "date", nullable: false),
                    DownloadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDataCoverage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketCandles_Symbol_Source_Date",
                table: "MarketCandles",
                columns: new[] { "Symbol", "Source", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketDataCoverage_Symbol_Source_PriceAdjustmentMode_From_To",
                table: "MarketDataCoverage",
                columns: new[] { "Symbol", "Source", "PriceAdjustmentMode", "From", "To" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketCandles");

            migrationBuilder.DropTable(
                name: "MarketDataCoverage");
        }
    }
}

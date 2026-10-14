using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AnalysisDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BacktestRunId = table.Column<int>(type: "int", nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Features = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: true),
                    StrategyConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FactorContributionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureSnapshots_BacktestRuns_BacktestRunId",
                        column: x => x.BacktestRunId,
                        principalTable: "BacktestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSnapshots_AnalysisDate",
                table: "FeatureSnapshots",
                column: "AnalysisDate");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSnapshots_BacktestRunId",
                table: "FeatureSnapshots",
                column: "BacktestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSnapshots_Symbol_AnalysisDate_StrategyId",
                table: "FeatureSnapshots",
                columns: new[] { "Symbol", "AnalysisDate", "StrategyId" },
                unique: true,
                filter: "[BacktestRunId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSnapshots_Symbol_AnalysisDate_StrategyId_BacktestRunId",
                table: "FeatureSnapshots",
                columns: new[] { "Symbol", "AnalysisDate", "StrategyId", "BacktestRunId" },
                unique: true,
                filter: "[BacktestRunId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureSnapshots");
        }
    }
}

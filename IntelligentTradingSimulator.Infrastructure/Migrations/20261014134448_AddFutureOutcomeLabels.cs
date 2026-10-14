using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFutureOutcomeLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FutureOutcomeLabels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FeatureSnapshotId = table.Column<int>(type: "int", nullable: false),
                    HorizonTradingDays = table.Column<int>(type: "int", nullable: false),
                    OutcomeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReturnPercent = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: false),
                    BenchmarkSymbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BenchmarkReturnPercent = table.Column<decimal>(type: "decimal(28,12)", precision: 28, scale: 12, nullable: true),
                    OutperformedBenchmark = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FutureOutcomeLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FutureOutcomeLabels_FeatureSnapshots_FeatureSnapshotId",
                        column: x => x.FeatureSnapshotId,
                        principalTable: "FeatureSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FutureOutcomeLabels_FeatureSnapshotId_HorizonTradingDays_BenchmarkSymbol",
                table: "FutureOutcomeLabels",
                columns: new[] { "FeatureSnapshotId", "HorizonTradingDays", "BenchmarkSymbol" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FutureOutcomeLabels");
        }
    }
}

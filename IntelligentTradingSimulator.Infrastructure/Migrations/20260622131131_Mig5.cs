using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Mig5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ObservationResults_ObservationDate",
                table: "ObservationResults");

            migrationBuilder.DropIndex(
                name: "IX_ObservationResults_ObservationDate_RankPosition",
                table: "ObservationResults");

            migrationBuilder.DropColumn(
                name: "ObservationDate",
                table: "ObservationResults");

            migrationBuilder.AddColumn<int>(
                name: "ObservationRunId",
                table: "ObservationResults",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ObservationRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_ObservationRunId",
                table: "ObservationResults",
                column: "ObservationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_RankPosition",
                table: "ObservationResults",
                column: "RankPosition");

            migrationBuilder.AddForeignKey(
                name: "FK_ObservationResults_ObservationRuns_ObservationRunId",
                table: "ObservationResults",
                column: "ObservationRunId",
                principalTable: "ObservationRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObservationResults_ObservationRuns_ObservationRunId",
                table: "ObservationResults");

            migrationBuilder.DropTable(
                name: "ObservationRuns");

            migrationBuilder.DropIndex(
                name: "IX_ObservationResults_ObservationRunId",
                table: "ObservationResults");

            migrationBuilder.DropIndex(
                name: "IX_ObservationResults_RankPosition",
                table: "ObservationResults");

            migrationBuilder.DropColumn(
                name: "ObservationRunId",
                table: "ObservationResults");

            migrationBuilder.AddColumn<DateTime>(
                name: "ObservationDate",
                table: "ObservationResults",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_ObservationDate",
                table: "ObservationResults",
                column: "ObservationDate");

            migrationBuilder.CreateIndex(
                name: "IX_ObservationResults_ObservationDate_RankPosition",
                table: "ObservationResults",
                columns: new[] { "ObservationDate", "RankPosition" });
        }
    }
}

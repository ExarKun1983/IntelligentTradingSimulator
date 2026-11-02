using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperExecutionSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperPortfolioStates",
                columns: table => new
                {
                    Identity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PortfolioId = table.Column<int>(type: "int", nullable: false),
                    LastCompletedSession = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperPortfolioStates", x => x.Identity);
                    table.ForeignKey(
                        name: "FK_PaperPortfolioStates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperPortfolioExecutions",
                columns: table => new
                {
                    Identity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperPortfolioExecutions", x => new { x.Identity, x.SessionDate });
                    table.ForeignKey(
                        name: "FK_PaperPortfolioExecutions_PaperPortfolioStates_Identity",
                        column: x => x.Identity,
                        principalTable: "PaperPortfolioStates",
                        principalColumn: "Identity",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperPortfolioStates_PortfolioId",
                table: "PaperPortfolioStates",
                column: "PortfolioId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperPortfolioExecutions");

            migrationBuilder.DropTable(
                name: "PaperPortfolioStates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBacktestPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PortfolioId",
                table: "BacktestRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_PortfolioId",
                table: "BacktestRuns",
                column: "PortfolioId");

            migrationBuilder.AddForeignKey(
                name: "FK_BacktestRuns_Portfolios_PortfolioId",
                table: "BacktestRuns",
                column: "PortfolioId",
                principalTable: "Portfolios",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BacktestRuns_Portfolios_PortfolioId",
                table: "BacktestRuns");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_PortfolioId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "BacktestRuns");
        }
    }
}

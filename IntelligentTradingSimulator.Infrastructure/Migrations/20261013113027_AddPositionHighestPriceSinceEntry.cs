using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionHighestPriceSinceEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HighestPriceSinceEntry",
                table: "Positions",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                """
                UPDATE [Positions]
                SET [HighestPriceSinceEntry] =
                    CASE
                        WHEN [AveragePrice] > [CurrentPrice] AND [AveragePrice] > 0 THEN [AveragePrice]
                        WHEN [CurrentPrice] > 0 THEN [CurrentPrice]
                        ELSE 0
                    END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighestPriceSinceEntry",
                table: "Positions");
        }
    }
}

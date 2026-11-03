using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntelligentTradingSimulator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperPortfolioAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AdoptedAtUtc",
                table: "PaperPortfolioStates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdoptionAuditReference",
                table: "PaperPortfolioStates",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AdoptionCutoffSession",
                table: "PaperPortfolioStates",
                type: "date",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaperPortfolioStates_Adoption",
                table: "PaperPortfolioStates",
                sql: "([AdoptionCutoffSession] IS NULL AND [AdoptionAuditReference] IS NULL AND [AdoptedAtUtc] IS NULL)\nOR ([AdoptionCutoffSession] IS NOT NULL AND [AdoptionCutoffSession] > '0001-01-01'\n    AND [AdoptionCutoffSession] < '9999-12-31'\n    AND [AdoptionAuditReference] IS NOT NULL AND TRIM([AdoptionAuditReference]) <> ''\n    AND [AdoptedAtUtc] IS NOT NULL\n    AND ([LastCompletedSession] IS NULL OR [LastCompletedSession] > [AdoptionCutoffSession]))");

            migrationBuilder.Sql("EXEC(N'" + """
                CREATE PROCEDURE [dbo].[AdoptPaperPortfolioUnknownHistory]
                    @ExpectedDatabase nvarchar(max),
                    @PortfolioId int,
                    @CutoffSession date,
                    @AuditReference nvarchar(max)
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;
                    IF @@TRANCOUNT <> 0
                        THROW 51020, 'Adoption requires a standalone transaction.', 1;
                    DECLARE @Whitespace nvarchar(32) = NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13)
                        + NCHAR(32) + NCHAR(133) + NCHAR(160) + NCHAR(5760)
                        + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196) + NCHAR(8197)
                        + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202)
                        + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);
                    IF @ExpectedDatabase IS NULL
                        OR @ExpectedDatabase COLLATE Latin1_General_100_BIN2 <> DB_NAME() COLLATE Latin1_General_100_BIN2
                        OR DATALENGTH(@ExpectedDatabase) <> DATALENGTH(DB_NAME())
                        THROW 51021, 'Explicit target database identity does not match.', 1;
                    IF @PortfolioId IS NULL OR @PortfolioId <= 0
                        OR @CutoffSession IS NULL OR @CutoffSession <= CONVERT(date, '00010101', 112)
                        OR @CutoffSession >= CONVERT(date, '99991231', 112)
                        OR @AuditReference IS NULL OR DATALENGTH(@AuditReference) > 2000
                        OR DATALENGTH(TRIM(
                            @Whitespace COLLATE Latin1_General_100_BIN2
                            FROM @AuditReference COLLATE Latin1_General_100_BIN2
                        )) = 0
                        THROW 51022, 'Explicit portfolio, usable cutoff and unknown-history audit reference are required.', 1;
                    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                    BEGIN TRY
                        BEGIN TRANSACTION;
                        DECLARE @lockResult int;
                        EXEC @lockResult = sys.sp_getapplock @Resource = N'PaperPortfolio:main',
                            @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
                        IF @lockResult < 0
                            THROW 51023, 'Paper adoption lock unavailable.', 1;
                        IF NOT EXISTS (SELECT 1 FROM dbo.Portfolios WHERE Id = @PortfolioId)
                            OR EXISTS (SELECT 1 FROM dbo.BacktestRuns WHERE PortfolioId = @PortfolioId)
                            OR EXISTS (SELECT 1 FROM dbo.PaperPortfolioStates
                                WHERE [Identity] = N'main' OR PortfolioId = @PortfolioId)
                            THROW 51024, 'Invalid/backtest portfolio or existing mapping; adoption refused without overwrite.', 1;
                        INSERT INTO dbo.PaperPortfolioStates
                            ([Identity], PortfolioId, LastCompletedSession,
                             AdoptionCutoffSession, AdoptionAuditReference, AdoptedAtUtc)
                        VALUES (N'main', @PortfolioId, NULL, @CutoffSession, @AuditReference, SYSUTCDATETIME());
                        COMMIT TRANSACTION;
                    END TRY
                    BEGIN CATCH
                        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                        THROW;
                    END CATCH;
                END;
                """.Replace("'", "''") + "');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE [dbo].[AdoptPaperPortfolioUnknownHistory];");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaperPortfolioStates_Adoption",
                table: "PaperPortfolioStates");

            migrationBuilder.DropColumn(
                name: "AdoptedAtUtc",
                table: "PaperPortfolioStates");

            migrationBuilder.DropColumn(
                name: "AdoptionAuditReference",
                table: "PaperPortfolioStates");

            migrationBuilder.DropColumn(
                name: "AdoptionCutoffSession",
                table: "PaperPortfolioStates");
        }
    }
}

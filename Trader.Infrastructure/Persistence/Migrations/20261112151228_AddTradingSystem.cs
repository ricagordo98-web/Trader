using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "InitialStopLoss",
                table: "JournalTrades",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TradeRecords",
                columns: table => new
                {
                    TradeId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    PlanVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SetupId = table.Column<long>(type: "INTEGER", nullable: true),
                    SetupName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ChecklistJson = table.Column<string>(type: "TEXT", nullable: false),
                    RuleBreaks = table.Column<int>(type: "INTEGER", nullable: false),
                    Justification = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DossierJson = table.Column<string>(type: "TEXT", nullable: true),
                    BreakEvenAtR = table.Column<double>(type: "REAL", nullable: false),
                    PartialAtR = table.Column<double>(type: "REAL", nullable: false),
                    MaxHoldingDays = table.Column<int>(type: "INTEGER", nullable: false),
                    BreakEvenAlerted = table.Column<bool>(type: "INTEGER", nullable: false),
                    PartialAlerted = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeAlerted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManagementFollowed = table.Column<bool>(type: "INTEGER", nullable: true),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FollowedPlan = table.Column<bool>(type: "INTEGER", nullable: true),
                    WentWell = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    WentWrong = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Lesson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    MaeR = table.Column<double>(type: "REAL", nullable: true),
                    MfeR = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeRecords", x => x.TradeId);
                    table.ForeignKey(
                        name: "FK_TradeRecords_JournalTrades_TradeId",
                        column: x => x.TradeId,
                        principalTable: "JournalTrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TradingPlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingSetups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    EntryCriteria = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ExitCriteria = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Checklist = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Image = table.Column<string>(type: "TEXT", nullable: true),
                    StrategyJson = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingSetups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: false),
                    WhatWorked = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    WhatFailed = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    RuleBreaks = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyReviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradeRecords_UserId",
                table: "TradeRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingPlans_UserId_Version",
                table: "TradingPlans",
                columns: new[] { "UserId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradingSetups_UserId",
                table: "TradingSetups",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyReviews_UserId_WeekStart",
                table: "WeeklyReviews",
                columns: new[] { "UserId", "WeekStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradeRecords");

            migrationBuilder.DropTable(
                name: "TradingPlans");

            migrationBuilder.DropTable(
                name: "TradingSetups");

            migrationBuilder.DropTable(
                name: "WeeklyReviews");

            migrationBuilder.DropColumn(
                name: "InitialStopLoss",
                table: "JournalTrades");
        }
    }
}

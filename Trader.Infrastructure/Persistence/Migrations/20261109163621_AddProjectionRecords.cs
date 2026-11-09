using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectionRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectionRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginTime = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetTime = table.Column<long>(type: "INTEGER", nullable: false),
                    HorizonMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPrice = table.Column<double>(type: "REAL", nullable: false),
                    ProjectedMedian = table.Column<double>(type: "REAL", nullable: false),
                    P10 = table.Column<double>(type: "REAL", nullable: false),
                    P25 = table.Column<double>(type: "REAL", nullable: false),
                    P75 = table.Column<double>(type: "REAL", nullable: false),
                    P90 = table.Column<double>(type: "REAL", nullable: false),
                    OneSigmaPercent = table.Column<double>(type: "REAL", nullable: false),
                    PlanDirection = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    PlanStop = table.Column<double>(type: "REAL", nullable: true),
                    PlanTarget = table.Column<double>(type: "REAL", nullable: true),
                    SuggestionId = table.Column<long>(type: "INTEGER", nullable: true),
                    ActualPrice = table.Column<double>(type: "REAL", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionRecords_CreatedAt",
                table: "ProjectionRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionRecords_Symbol_TargetTime_HorizonMinutes",
                table: "ProjectionRecords",
                columns: new[] { "Symbol", "TargetTime", "HorizonMinutes" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionRecords");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class OrganizeDoePumpPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "report_id",
                table: "doe_fuel_price",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "doe_import_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: true),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    active_slot = table.Column<int>(type: "integer", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reports_found = table.Column<int>(type: "integer", nullable: false),
                    reports_imported = table.Column<int>(type: "integer", nullable: false),
                    reports_skipped = table.Column<int>(type: "integer", nullable: false),
                    reports_failed = table.Column<int>(type: "integer", nullable: false),
                    price_rows_added = table.Column<int>(type: "integer", nullable: false),
                    price_rows_updated = table.Column<int>(type: "integer", nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doe_import_job", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "doe_pump_price_report",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subdivision = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    week_end = table.Column<DateOnly>(type: "date", nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    imported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    price_rows = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doe_pump_price_report", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_doe_fuel_price_report_id",
                table: "doe_fuel_price",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_doe_import_job_active_slot",
                table: "doe_import_job",
                column: "active_slot",
                unique: true,
                filter: "active_slot IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_doe_import_job_status_created_at_utc",
                table: "doe_import_job",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_doe_pump_price_report_section_subdivision_week_start",
                table: "doe_pump_price_report",
                columns: new[] { "section", "subdivision", "week_start" });

            migrationBuilder.CreateIndex(
                name: "IX_doe_pump_price_report_source_url_week_start",
                table: "doe_pump_price_report",
                columns: new[] { "source_url", "week_start" },
                unique: true);

            // Preserve city imports created before reports were modeled explicitly.
            migrationBuilder.Sql("""
                INSERT INTO doe_pump_price_report
                    (section, subdivision, week_start, week_end, source_url,
                     content_hash, imported_at_utc, price_rows, status)
                SELECT 'legacy', NULL, week_start, MAX(week_end), source_url,
                       NULL, MAX(fetched_at_utc), COUNT(*)::integer, 'partial'
                FROM doe_fuel_price
                GROUP BY source_url, week_start;

                UPDATE doe_fuel_price AS price
                SET report_id = report.id
                FROM doe_pump_price_report AS report
                WHERE price.source_url = report.source_url
                  AND price.week_start = report.week_start;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_doe_fuel_price_doe_pump_price_report_report_id",
                table: "doe_fuel_price",
                column: "report_id",
                principalTable: "doe_pump_price_report",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_doe_fuel_price_doe_pump_price_report_report_id",
                table: "doe_fuel_price");

            migrationBuilder.DropTable(
                name: "doe_import_job");

            migrationBuilder.DropTable(
                name: "doe_pump_price_report");

            migrationBuilder.DropIndex(
                name: "IX_doe_fuel_price_report_id",
                table: "doe_fuel_price");

            migrationBuilder.DropColumn(
                name: "report_id",
                table: "doe_fuel_price");
        }
    }
}

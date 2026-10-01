using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFuelAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fuel_adjustment",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    week_end = table.Column<DateOnly>(type: "date", nullable: false),
                    oil_company = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    effective_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    gasoline_change_per_liter = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    diesel_change_per_liter = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    kerosene_change_per_liter = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    fetched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_adjustment", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_at_utc",
                table: "fuel_adjustment",
                columns: new[] { "week_start", "oil_company", "effective_at_utc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fuel_adjustment");
        }
    }
}

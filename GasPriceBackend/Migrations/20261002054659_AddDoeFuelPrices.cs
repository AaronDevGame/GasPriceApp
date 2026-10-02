using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDoeFuelPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "doe_fuel_price",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    week_end = table.Column<DateOnly>(type: "date", nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    city_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    province = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    province_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    oil_company = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fuel_grade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    min_price_per_liter = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    max_price_per_liter = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    fetched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doe_fuel_price", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_doe_fuel_price_city_key_province_key_week_end",
                table: "doe_fuel_price",
                columns: new[] { "city_key", "province_key", "week_end" });

            migrationBuilder.CreateIndex(
                name: "IX_doe_fuel_price_week_start_city_key_province_key_oil_company~",
                table: "doe_fuel_price",
                columns: new[] { "week_start", "city_key", "province_key", "oil_company", "fuel_grade" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doe_fuel_price");
        }
    }
}

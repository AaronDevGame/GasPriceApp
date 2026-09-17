using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFuelPriceCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fuel_price_cache",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    province = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    city_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    province_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cached_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    refresh_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fuel_price_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fuel_price_cache_scope_province_key_city_key_cached_at",
                table: "fuel_price_cache",
                columns: new[] { "scope", "province_key", "city_key", "cached_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fuel_price_cache");
        }
    }
}

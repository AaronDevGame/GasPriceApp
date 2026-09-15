using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFuelPriceRegionCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "region",
                table: "fuel_price_cache",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "region_key",
                table: "fuel_price_cache",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fuel_price_cache_scope_region_key_cached_at",
                table: "fuel_price_cache",
                columns: new[] { "scope", "region_key", "cached_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fuel_price_cache_scope_region_key_cached_at",
                table: "fuel_price_cache");

            migrationBuilder.DropColumn(
                name: "region",
                table: "fuel_price_cache");

            migrationBuilder.DropColumn(
                name: "region_key",
                table: "fuel_price_cache");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDoePageCacheMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "listing_week_start",
                table: "doe_page_cache",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "section",
                table: "doe_page_cache",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_url",
                table: "doe_page_cache",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subdivision",
                table: "doe_page_cache",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_doe_page_cache_section_subdivision_listing_week_start",
                table: "doe_page_cache",
                columns: new[] { "section", "subdivision", "listing_week_start" });

            migrationBuilder.CreateIndex(
                name: "IX_doe_page_cache_source_url",
                table: "doe_page_cache",
                column: "source_url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_doe_page_cache_section_subdivision_listing_week_start",
                table: "doe_page_cache");

            migrationBuilder.DropIndex(
                name: "IX_doe_page_cache_source_url",
                table: "doe_page_cache");

            migrationBuilder.DropColumn(
                name: "listing_week_start",
                table: "doe_page_cache");

            migrationBuilder.DropColumn(
                name: "section",
                table: "doe_page_cache");

            migrationBuilder.DropColumn(
                name: "source_url",
                table: "doe_page_cache");

            migrationBuilder.DropColumn(
                name: "subdivision",
                table: "doe_page_cache");
        }
    }
}

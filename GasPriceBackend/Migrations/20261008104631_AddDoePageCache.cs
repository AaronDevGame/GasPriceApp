using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDoePageCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "doe_page_cache",
                columns: table => new
                {
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    extractor_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    extraction_json = table.Column<string>(type: "jsonb", nullable: false),
                    extracted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doe_page_cache", x => new { x.content_hash, x.extractor_version, x.page_number });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "doe_page_cache");
        }
    }
}

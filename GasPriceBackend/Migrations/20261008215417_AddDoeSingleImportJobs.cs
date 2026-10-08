using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDoeSingleImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_json",
                table: "doe_import_job",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result_json",
                table: "doe_import_job",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "request_json",
                table: "doe_import_job");

            migrationBuilder.DropColumn(
                name: "result_json",
                table: "doe_import_job");
        }
    }
}

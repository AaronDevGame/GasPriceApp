using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddDoeMonitoringDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "monitoring_end",
                table: "doe_fuel_price",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "monitoring_start",
                table: "doe_fuel_price",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "monitoring_end",
                table: "doe_fuel_price");

            migrationBuilder.DropColumn(
                name: "monitoring_start",
                table: "doe_fuel_price");
        }
    }
}

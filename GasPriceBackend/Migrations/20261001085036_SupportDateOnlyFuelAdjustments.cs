using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class SupportDateOnlyFuelAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_at_utc",
                table: "fuel_adjustment");

            migrationBuilder.AlterColumn<DateTime>(
                name: "effective_at_utc",
                table: "fuel_adjustment",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_date_philippines",
                table: "fuel_adjustment",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE fuel_adjustment
                SET effective_date_philippines = (effective_at_utc AT TIME ZONE 'Asia/Manila')::date
                WHERE effective_date_philippines IS NULL;
                """);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "effective_date_philippines",
                table: "fuel_adjustment",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_at_utc",
                table: "fuel_adjustment",
                columns: new[] { "week_start", "oil_company", "effective_at_utc" },
                unique: true,
                filter: "effective_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_date_phili~",
                table: "fuel_adjustment",
                columns: new[] { "week_start", "oil_company", "effective_date_philippines" },
                unique: true,
                filter: "effective_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_at_utc",
                table: "fuel_adjustment");

            migrationBuilder.DropIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_date_phili~",
                table: "fuel_adjustment");

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM fuel_adjustment WHERE effective_at_utc IS NULL) THEN
                        RAISE EXCEPTION 'Cannot roll back date-only fuel adjustments without losing effectivity';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "effective_date_philippines",
                table: "fuel_adjustment");

            migrationBuilder.AlterColumn<DateTime>(
                name: "effective_at_utc",
                table: "fuel_adjustment",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fuel_adjustment_week_start_oil_company_effective_at_utc",
                table: "fuel_adjustment",
                columns: new[] { "week_start", "oil_company", "effective_at_utc" },
                unique: true);
        }
    }
}

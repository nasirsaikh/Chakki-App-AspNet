using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chakki.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartialDeliveryUdhaar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OldUdhaarPart",
                table: "Payments",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "GrindingOrderLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GrindingOrderLineId",
                table: "Buybacks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OldUdhaarPart",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "GrindingOrderLines");

            migrationBuilder.DropColumn(
                name: "GrindingOrderLineId",
                table: "Buybacks");
        }
    }
}

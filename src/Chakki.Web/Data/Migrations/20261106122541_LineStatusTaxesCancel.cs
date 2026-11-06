using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chakki.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class LineStatusTaxesCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "Sales",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxName",
                table: "Sales",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxPercent",
                table: "Sales",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "TaxRateId",
                table: "RateCards",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaxRateId",
                table: "InventoryItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "GrindingOrders",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "GrindingOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxTotal",
                table: "GrindingOrders",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "DoneAt",
                table: "GrindingOrderLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "GrindingOrderLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "GrindingOrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxName",
                table: "GrindingOrderLines",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxPercent",
                table: "GrindingOrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Percent = table.Column<decimal>(type: "TEXT", nullable: false),
                    Inclusive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SplitInHalves = table.Column<bool>(type: "INTEGER", nullable: false),
                    HalfLabelA = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    HalfLabelB = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxRates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RateCards_TaxRateId",
                table: "RateCards",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TaxRateId",
                table: "InventoryItems",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_OrganizationId",
                table: "TaxRates",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryItems_TaxRates_TaxRateId",
                table: "InventoryItems",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RateCards_TaxRates_TaxRateId",
                table: "RateCards",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryItems_TaxRates_TaxRateId",
                table: "InventoryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_RateCards_TaxRates_TaxRateId",
                table: "RateCards");

            migrationBuilder.DropTable(
                name: "TaxRates");

            migrationBuilder.DropIndex(
                name: "IX_RateCards_TaxRateId",
                table: "RateCards");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_TaxRateId",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxName",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxPercent",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "RateCards");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "GrindingOrders");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "GrindingOrders");

            migrationBuilder.DropColumn(
                name: "TaxTotal",
                table: "GrindingOrders");

            migrationBuilder.DropColumn(
                name: "DoneAt",
                table: "GrindingOrderLines");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "GrindingOrderLines");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "GrindingOrderLines");

            migrationBuilder.DropColumn(
                name: "TaxName",
                table: "GrindingOrderLines");

            migrationBuilder.DropColumn(
                name: "TaxPercent",
                table: "GrindingOrderLines");
        }
    }
}

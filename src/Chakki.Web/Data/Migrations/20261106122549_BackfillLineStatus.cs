using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chakki.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing orders: each line follows its order's status (Grinding = 1, Ready = 2, Delivered = 3).
            migrationBuilder.Sql("UPDATE GrindingOrderLines SET Status = 2 WHERE GrindingOrderId IN (SELECT Id FROM GrindingOrders WHERE Status IN (2, 3));");
            migrationBuilder.Sql("UPDATE GrindingOrderLines SET Status = 1 WHERE GrindingOrderId IN (SELECT Id FROM GrindingOrders WHERE Status = 1);");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

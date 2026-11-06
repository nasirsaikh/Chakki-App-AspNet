using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chakki.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDeliveredLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orders handed over before grains could be handed over one by one: every grain of a delivered order was delivered.
            migrationBuilder.Sql("UPDATE GrindingOrderLines SET Status = 3, DeliveredAt = (SELECT DeliveredAt FROM GrindingOrders o WHERE o.Id = GrindingOrderId) WHERE GrindingOrderId IN (SELECT Id FROM GrindingOrders WHERE Status = 3);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE GrindingOrderLines SET Status = 2, DeliveredAt = NULL WHERE Status = 3;");
        }
    }
}

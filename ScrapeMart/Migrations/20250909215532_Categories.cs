using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScrapeMart.Migrations
{
    /// <inheritdoc />
    public partial class Categories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VtexRetailersConfig_RetailerId",
                table: "VtexRetailersConfig");

            migrationBuilder.DropColumn(
                name: "IdBandera",
                table: "VtexRetailersConfig");

            migrationBuilder.DropColumn(
                name: "IdComercio",
                table: "VtexRetailersConfig");

            migrationBuilder.AlterColumn<string>(
                name: "SalesChannels",
                table: "VtexRetailersConfig",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "RetailerId",
                table: "VtexRetailersConfig",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "RetailerHost",
                table: "VtexRetailersConfig",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);


            migrationBuilder.AddColumn<string>(
                name: "RetailerHost",
                table: "Categories",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            

            migrationBuilder.CreateIndex(
                name: "IX_Products_RetailerHost_ProductId",
                table: "Products",
                columns: new[] { "RetailerHost", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_RetailerHost_CategoryId",
                table: "Categories",
                columns: new[] { "RetailerHost", "CategoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropTable(
                name: "VtexPickupPoints");

            migrationBuilder.DropIndex(
                name: "IX_Products_RetailerHost_ProductId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Categories_RetailerHost_CategoryId",
                table: "Categories");

           
            migrationBuilder.DropColumn(
                name: "RetailerHost",
                table: "Categories");

            migrationBuilder.AlterColumn<string>(
                name: "SalesChannels",
                table: "VtexRetailersConfig",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "RetailerId",
                table: "VtexRetailersConfig",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "RetailerHost",
                table: "VtexRetailersConfig",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "IdBandera",
                table: "VtexRetailersConfig",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IdComercio",
                table: "VtexRetailersConfig",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_VtexRetailersConfig_RetailerId",
                table: "VtexRetailersConfig",
                column: "RetailerId",
                unique: true);
        }
    }
}

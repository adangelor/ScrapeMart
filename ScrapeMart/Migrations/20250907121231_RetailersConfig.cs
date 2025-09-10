using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScrapeMart.Migrations
{
    /// <inheritdoc />
    public partial class RetailersConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VtexRetailersConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RetailerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RetailerHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SalesChannels = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IdBandera = table.Column<int>(type: "int", nullable: false),
                    IdComercio = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VtexRetailersConfig", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VtexRetailersConfig_RetailerId",
                table: "VtexRetailersConfig",
                column: "RetailerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VtexRetailersConfig");
        }
    }
}

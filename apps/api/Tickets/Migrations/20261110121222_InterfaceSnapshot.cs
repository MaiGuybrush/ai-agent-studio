using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class InterfaceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "interface_snapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpecId = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ServerKey = table.Column<string>(type: "text", nullable: true),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    InputSchemaJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputSchemaJson = table.Column<string>(type: "jsonb", nullable: true),
                    CheckedByEmpNo = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interface_snapshot", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_interface_snapshot_TicketId_SpecId",
                table: "interface_snapshot",
                columns: new[] { "TicketId", "SpecId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interface_snapshot");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RD.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MetaShadowComparison : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetCampaignIdsJson",
                table: "Decisions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MetaActivityFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AdAccountId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EventTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ObjectId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ObjectName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ObjectType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApplicationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApplicationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Tool = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TranslatedEventType = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OldStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ExtraDataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaActivityFacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaShadowPredictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProposedAction = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    DesiredStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TargetState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaShadowPredictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaShadowPredictions_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MetaShadowPredictions_Decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "Decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaActivityFacts_EventType_EventTime",
                table: "MetaActivityFacts",
                columns: new[] { "EventType", "EventTime" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaActivityFacts_ObjectId_EventTime",
                table: "MetaActivityFacts",
                columns: new[] { "ObjectId", "EventTime" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaActivityFacts_SourceFingerprint",
                table: "MetaActivityFacts",
                column: "SourceFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaShadowPredictions_CampaignId_ProposedAction_StartedAt",
                table: "MetaShadowPredictions",
                columns: new[] { "CampaignId", "ProposedAction", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaShadowPredictions_ClientId_CampaignId_ProposedAction_TargetState",
                table: "MetaShadowPredictions",
                columns: new[] { "ClientId", "CampaignId", "ProposedAction", "TargetState" },
                unique: true,
                filter: "[EndedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MetaShadowPredictions_ClientId_StartedAt",
                table: "MetaShadowPredictions",
                columns: new[] { "ClientId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaShadowPredictions_DecisionId",
                table: "MetaShadowPredictions",
                column: "DecisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaActivityFacts");

            migrationBuilder.DropTable(
                name: "MetaShadowPredictions");

            migrationBuilder.DropColumn(
                name: "TargetCampaignIdsJson",
                table: "Decisions");
        }
    }
}

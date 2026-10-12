using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaStreamSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StreamInspections",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamInspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StreamInspections_MovieImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "media",
                        principalTable: "MovieImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StreamInventories",
                schema: "media",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamInventories", x => x.AssetId);
                    table.UniqueConstraint("AK_StreamInventories_AssetId_Version", x => new { x.AssetId, x.Version });
                    table.ForeignKey(
                        name: "FK_StreamInventories_MediaAssets_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StreamSelectionAudits",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeforeVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    AfterVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoIndex = table.Column<int>(type: "integer", nullable: false),
                    AudioIndex = table.Column<int>(type: "integer", nullable: true),
                    SubtitleIndex = table.Column<int>(type: "integer", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamSelectionAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StreamSelectionAudits_MovieImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "media",
                        principalTable: "MovieImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MediaStreams",
                schema: "media",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Details_Index = table.Column<int>(type: "integer", nullable: false),
                    Details_Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Details_Codec = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details_Language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details_DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Details_IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Details_IsForced = table.Column<bool>(type: "boolean", nullable: false),
                    Details_IsAttachedPicture = table.Column<bool>(type: "boolean", nullable: false),
                    Details_Width = table.Column<int>(type: "integer", nullable: true),
                    Details_Height = table.Column<int>(type: "integer", nullable: true),
                    Details_FrameRate = table.Column<double>(type: "double precision", nullable: true),
                    Details_Channels = table.Column<int>(type: "integer", nullable: true),
                    Details_SampleRate = table.Column<int>(type: "integer", nullable: true),
                    Details_ReportedType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaStreams", x => new { x.AssetId, x.Index });
                    table.CheckConstraint("CK_Stream_Index", "\"Index\" >= 0 AND \"Details_Index\" = \"Index\"");
                    table.ForeignKey(
                        name: "FK_MediaStreams_StreamInventories_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "media",
                        principalTable: "StreamInventories",
                        principalColumn: "AssetId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StreamSelections",
                schema: "media",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoIndex = table.Column<int>(type: "integer", nullable: true),
                    AudioIndex = table.Column<int>(type: "integer", nullable: true),
                    SubtitleIndex = table.Column<int>(type: "integer", nullable: true),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SelectedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamSelections", x => x.ImportId);
                    table.CheckConstraint("CK_Selection_State", "(\"VideoIndex\" IS NULL AND \"AudioIndex\" IS NULL AND \"SubtitleIndex\" IS NULL AND \"SelectedAt\" IS NULL AND \"SelectedBy\" IS NULL) OR (\"VideoIndex\" IS NOT NULL AND \"VideoIndex\" >= 0 AND \"SelectedAt\" IS NOT NULL AND \"SelectedBy\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_StreamSelections_MovieImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "media",
                        principalTable: "MovieImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StreamSelections_StreamInventories_AssetId_InventoryVersion",
                        columns: x => new { x.AssetId, x.InventoryVersion },
                        principalSchema: "media",
                        principalTable: "StreamInventories",
                        principalColumns: new[] { "AssetId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StreamInspections_ImportId_IdempotencyKey",
                schema: "media",
                table: "StreamInspections",
                columns: new[] { "ImportId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StreamInspections_JobId",
                schema: "media",
                table: "StreamInspections",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StreamSelectionAudits_ImportId",
                schema: "media",
                table: "StreamSelectionAudits",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSelectionAudits_Sequence",
                schema: "media",
                table: "StreamSelectionAudits",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StreamSelections_AssetId_InventoryVersion",
                schema: "media",
                table: "StreamSelections",
                columns: new[] { "AssetId", "InventoryVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaStreams",
                schema: "media");

            migrationBuilder.DropTable(
                name: "StreamInspections",
                schema: "media");

            migrationBuilder.DropTable(
                name: "StreamSelectionAudits",
                schema: "media");

            migrationBuilder.DropTable(
                name: "StreamSelections",
                schema: "media");

            migrationBuilder.DropTable(
                name: "StreamInventories",
                schema: "media");
        }
    }
}

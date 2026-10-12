using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualMediaImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Metadata_Container = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Metadata_DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Metadata_VideoCodec = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Metadata_Width = table.Column<int>(type: "integer", nullable: false),
                    Metadata_Height = table.Column<int>(type: "integer", nullable: false),
                    Metadata_FrameRate = table.Column<double>(type: "double precision", nullable: false),
                    Metadata_AudioCodec = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Metadata_AudioChannels = table.Column<int>(type: "integer", nullable: true),
                    Metadata_AudioSampleRate = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.CheckConstraint("CK_Asset_Fingerprint", "\"SizeBytes\" > 0 AND \"Sha256\" ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "MovieImports",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Folder = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Source = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieImports", x => x.Id);
                    table.CheckConstraint("CK_Import_Completion", "(\"AssetId\" IS NULL AND \"CompletedAt\" IS NULL AND NOT \"IsDuplicate\") OR (\"AssetId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MovieImports_MediaAssets_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "media",
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MediaRights",
                schema: "media",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Details_Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Details_Authorization = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Details_LicenseEvidence = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Details_PermittedUses = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Details_PermittedPlatforms = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Details_Attribution = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Details_ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRights", x => x.ImportId);
                    table.ForeignKey(
                        name: "FK_MediaRights_MovieImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "media",
                        principalTable: "MovieImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Sha256",
                schema: "media",
                table: "MediaAssets",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieImports_AssetId",
                schema: "media",
                table: "MovieImports",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieImports_IdempotencyKey",
                schema: "media",
                table: "MovieImports",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieImports_JobId",
                schema: "media",
                table: "MovieImports",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaRights",
                schema: "media");

            migrationBuilder.DropTable(
                name: "MovieImports",
                schema: "media");

            migrationBuilder.DropTable(
                name: "MediaAssets",
                schema: "media");
        }
    }
}

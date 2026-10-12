using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieRecap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MediaDiscoveryInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpectedFingerprint",
                schema: "media",
                table: "MovieImports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaDiscoveries",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Folder = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PathKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StableSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaDiscoveries", x => x.Id);
                    table.CheckConstraint("CK_Discovery_Fingerprint", "\"SizeBytes\" >= 0 AND \"Fingerprint\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_Discovery_Submission", "(\"Status\" = 'Submitted' AND \"ImportId\" IS NOT NULL AND \"SubmittedVersion\" IS NOT NULL) OR (\"Status\" <> 'Submitted' AND \"ImportId\" IS NULL AND \"SubmittedVersion\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_MediaDiscoveries_MovieImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "media",
                        principalTable: "MovieImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaDiscoveries_Folder_PathKey",
                schema: "media",
                table: "MediaDiscoveries",
                columns: new[] { "Folder", "PathKey" },
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "IX_MediaDiscoveries_ImportId",
                schema: "media",
                table: "MediaDiscoveries",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaDiscoveries_Status_FirstSeenAt",
                schema: "media",
                table: "MediaDiscoveries",
                columns: new[] { "Status", "FirstSeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaDiscoveries",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "ExpectedFingerprint",
                schema: "media",
                table: "MovieImports");
        }
    }
}

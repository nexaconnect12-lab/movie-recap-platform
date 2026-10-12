using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<ProcessingJob> Jobs => Set<ProcessingJob>();
    public DbSet<JobAudit> Audits => Set<JobAudit>();
    public DbSet<MovieImport> Imports => Set<MovieImport>();
    public DbSet<MediaAsset> Assets => Set<MediaAsset>();
    public DbSet<ImportRightsRecord> ImportRights => Set<ImportRightsRecord>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var j = model.Entity<ProcessingJob>();
        j.ToTable("ProcessingJobs", "jobs"); j.HasKey(x => x.Id);
        j.Property(x => x.Kind).HasMaxLength(64);
        j.Property(x => x.IdempotencyKey).HasMaxLength(128);
        j.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        j.Property(x => x.WorkerId).HasMaxLength(128);
        j.Property(x => x.ErrorCode).HasMaxLength(128);
        j.Property(x => x.Version).IsConcurrencyToken();
        j.HasIndex(x => x.IdempotencyKey).IsUnique();
        j.HasIndex(x => new { x.Status, x.AvailableAt });
        j.HasIndex(x => x.LeaseExpiresAt);
        j.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Job_Progress", "\"Progress\" BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Job_Attempts", "\"Attempts\" >= 0 AND \"MaxAttempts\" BETWEEN 1 AND 5");
        });
        var a = model.Entity<JobAudit>(); a.ToTable("AuditLogs", "jobs"); a.HasKey(x => x.Id);
        a.Property(x => x.Sequence).UseIdentityAlwaysColumn();
        a.HasIndex(x => x.Sequence).IsUnique();
        a.Property(x => x.Action).HasMaxLength(64); a.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        a.HasIndex(x => new { x.JobId, x.At }); a.HasOne<ProcessingJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        var i = model.Entity<MovieImport>(); i.ToTable("MovieImports", "media"); i.HasKey(x => x.Id);
        i.Property(x => x.IdempotencyKey).HasMaxLength(128); i.HasIndex(x => x.IdempotencyKey).IsUnique();
        i.Property(x => x.Folder).HasMaxLength(64); i.Property(x => x.RelativePath).HasMaxLength(1024);
        i.Property(x => x.Source).HasMaxLength(2048); i.Property(x => x.RequestedBy).HasMaxLength(256);
        i.HasIndex(x => x.JobId).IsUnique();
        i.HasOne<MediaAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        i.ToTable(t => t.HasCheckConstraint("CK_Import_Completion", "(\"AssetId\" IS NULL AND \"CompletedAt\" IS NULL AND NOT \"IsDuplicate\") OR (\"AssetId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)"));
        var asset = model.Entity<MediaAsset>(); asset.ToTable("MediaAssets", "media"); asset.HasKey(x => x.Id);
        asset.Property(x => x.Sha256).HasMaxLength(64); asset.HasIndex(x => x.Sha256).IsUnique();
        asset.ToTable(t => t.HasCheckConstraint("CK_Asset_Fingerprint", "\"SizeBytes\" > 0 AND \"Sha256\" ~ '^[0-9a-f]{64}$'"));
        asset.OwnsOne(x => x.Metadata, m =>
        {
            m.Property(x => x.Container).HasMaxLength(128); m.Property(x => x.VideoCodec).HasMaxLength(64); m.Property(x => x.AudioCodec).HasMaxLength(64);
        });
        asset.Navigation(x => x.Metadata).IsRequired();
        var r = model.Entity<ImportRightsRecord>(); r.ToTable("MediaRights", "media"); r.HasKey(x => x.ImportId);
        r.HasOne<MovieImport>().WithOne().HasForeignKey<ImportRightsRecord>(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        r.OwnsOne(x => x.Details, d =>
        {
            d.Property(x => x.Owner).HasMaxLength(256); d.Property(x => x.Authorization).HasConversion<string>().HasMaxLength(32);
            d.Property(x => x.LicenseEvidence).HasMaxLength(2048); d.Property(x => x.PermittedUses).HasMaxLength(2048);
            d.Property(x => x.PermittedPlatforms).HasMaxLength(2048); d.Property(x => x.Attribution).HasMaxLength(2048);
        });
        r.Navigation(x => x.Details).IsRequired();
    }
}
public sealed class DesignFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Platform")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Platform for EF commands.");
        return new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connection).Options);
    }
}


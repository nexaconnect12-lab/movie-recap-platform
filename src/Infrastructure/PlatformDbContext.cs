using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MovieRecap.Jobs.Domain;
using MovieRecap.Media.Domain;
using MovieRecap.VideoProcessing.Domain;
namespace MovieRecap.Infrastructure;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<ProcessingJob> Jobs => Set<ProcessingJob>();
    public DbSet<JobAudit> Audits => Set<JobAudit>();
    public DbSet<MovieImport> Imports => Set<MovieImport>();
    public DbSet<MediaAsset> Assets => Set<MediaAsset>();
    public DbSet<ImportRightsRecord> ImportRights => Set<ImportRightsRecord>();
    public DbSet<MediaDiscovery> Discoveries => Set<MediaDiscovery>();
    public DbSet<StreamInventoryRecord> StreamInventories => Set<StreamInventoryRecord>();
    public DbSet<MediaStreamRecord> MediaStreams => Set<MediaStreamRecord>();
    public DbSet<StreamSelection> StreamSelections => Set<StreamSelection>();
    public DbSet<StreamInspectionRecord> StreamInspections => Set<StreamInspectionRecord>();
    public DbSet<SelectionAuditRecord> SelectionAudits => Set<SelectionAuditRecord>();
    public DbSet<SceneAnalysis> SceneAnalyses => Set<SceneAnalysis>();
    public DbSet<MovieScene> MovieScenes => Set<MovieScene>();
    public DbSet<AudioExtraction> AudioExtractions => Set<AudioExtraction>();
    public DbSet<FrameSampling> FrameSamplings => Set<FrameSampling>();
    public DbSet<FrameSample> FrameSamples => Set<FrameSample>();
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
        i.Property(x => x.ExpectedFingerprint).HasMaxLength(64);
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
        var discovery = model.Entity<MediaDiscovery>(); discovery.ToTable("MediaDiscoveries", "media"); discovery.HasKey(x => x.Id);
        discovery.Property(x => x.Folder).HasMaxLength(64); discovery.Property(x => x.RelativePath).HasMaxLength(1024);
        discovery.Property(x => x.PathKey).HasMaxLength(64); discovery.Property(x => x.Fingerprint).HasMaxLength(64);
        discovery.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); discovery.Property(x => x.ErrorCode).HasMaxLength(128);
        discovery.Property(x => x.Version).IsConcurrencyToken();
        discovery.HasIndex(x => new { x.Folder, x.PathKey }).IsUnique().HasFilter("\"IsCurrent\"");
        discovery.HasIndex(x => new { x.Status, x.FirstSeenAt });
        discovery.HasOne<MovieImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        discovery.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Discovery_Fingerprint", "\"SizeBytes\" >= 0 AND \"Fingerprint\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_Discovery_Submission", "(\"Status\" = 'Submitted' AND \"ImportId\" IS NOT NULL AND \"SubmittedVersion\" IS NOT NULL) OR (\"Status\" <> 'Submitted' AND \"ImportId\" IS NULL AND \"SubmittedVersion\" IS NULL)");
        });
        var inventory = model.Entity<StreamInventoryRecord>(); inventory.ToTable("StreamInventories", "media"); inventory.HasKey(x => x.AssetId);
        inventory.HasAlternateKey(x => new { x.AssetId, x.Version }); inventory.HasOne<MediaAsset>().WithOne().HasForeignKey<StreamInventoryRecord>(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        var stream = model.Entity<MediaStreamRecord>(); stream.ToTable("MediaStreams", "media"); stream.HasKey(x => new { x.AssetId, x.Index });
        stream.HasOne<StreamInventoryRecord>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        stream.OwnsOne(x => x.Details, d =>
        {
            d.Ignore(x => x.IsSelectable); d.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            d.Property(x => x.Codec).HasMaxLength(64); d.Property(x => x.Language).HasMaxLength(64); d.Property(x => x.ReportedType).HasMaxLength(32);
        });
        stream.Navigation(x => x.Details).IsRequired();
        stream.ToTable(t => t.HasCheckConstraint("CK_Stream_Index", "\"Index\" >= 0 AND \"Details_Index\" = \"Index\""));
        var selection = model.Entity<StreamSelection>(); selection.ToTable("StreamSelections", "media"); selection.HasKey(x => x.ImportId);
        selection.Property(x => x.Version).IsConcurrencyToken(); selection.Property(x => x.SelectedBy).HasMaxLength(256);
        selection.HasOne<MovieImport>().WithOne().HasForeignKey<StreamSelection>(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        selection.HasOne<StreamInventoryRecord>().WithMany().HasForeignKey(x => new { x.AssetId, x.InventoryVersion }).HasPrincipalKey(x => new { x.AssetId, x.Version }).OnDelete(DeleteBehavior.Restrict);
        selection.ToTable(t => t.HasCheckConstraint("CK_Selection_State", "(\"VideoIndex\" IS NULL AND \"AudioIndex\" IS NULL AND \"SubtitleIndex\" IS NULL AND \"SelectedAt\" IS NULL AND \"SelectedBy\" IS NULL) OR (\"VideoIndex\" IS NOT NULL AND \"VideoIndex\" >= 0 AND \"SelectedAt\" IS NOT NULL AND \"SelectedBy\" IS NOT NULL)"));
        var inspection = model.Entity<StreamInspectionRecord>(); inspection.ToTable("StreamInspections", "media"); inspection.HasKey(x => x.Id);
        inspection.Property(x => x.IdempotencyKey).HasMaxLength(128); inspection.HasIndex(x => new { x.ImportId, x.IdempotencyKey }).IsUnique(); inspection.HasIndex(x => x.JobId).IsUnique();
        inspection.HasOne<MovieImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<SelectionAuditRecord>(); audit.ToTable("StreamSelectionAudits", "media"); audit.HasKey(x => x.Id);
        audit.Property(x => x.Sequence).UseIdentityAlwaysColumn(); audit.HasIndex(x => x.Sequence).IsUnique(); audit.Property(x => x.RequestedBy).HasMaxLength(256);
        audit.HasIndex(x => x.ImportId); audit.HasOne<MovieImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        var analysis = model.Entity<SceneAnalysis>(); analysis.ToTable("SceneAnalyses", "video"); analysis.HasKey(x => x.Id);
        analysis.Property(x => x.SourceSha256).HasMaxLength(64); analysis.Property(x => x.IdempotencyKey).HasMaxLength(128); analysis.Property(x => x.RequestedBy).HasMaxLength(256);
        foreach (var field in new[] { nameof(SceneAnalysis.TimingMode), nameof(SceneAnalysis.SceneDetectVersion), nameof(SceneAnalysis.PyAvVersion), nameof(SceneAnalysis.OpenCvVersion), nameof(SceneAnalysis.PythonVersion), nameof(SceneAnalysis.Algorithm) }) analysis.Property(field).HasMaxLength(64);
        analysis.HasIndex(x => new { x.ImportId, x.IdempotencyKey }).IsUnique(); analysis.HasIndex(x => x.JobId).IsUnique(); analysis.HasIndex(x => new { x.ImportId, x.CreatedAt });
        analysis.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Analysis_Binding", "\"VideoIndex\" >= 0 AND \"SourceSizeBytes\" > 0 AND \"SourceSha256\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_Analysis_Settings", "\"Threshold\" BETWEEN 1 AND 255 AND \"MinimumSceneFrames\" BETWEEN 1 AND 3000 AND \"MaxAttempts\" BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_Analysis_Result", "(\"CompletedAt\" IS NULL AND \"TotalFrames\" IS NULL AND \"DurationSeconds\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"TotalFrames\" IS NOT NULL AND \"TotalFrames\" > 0 AND \"DurationSeconds\" IS NOT NULL AND \"DurationSeconds\" > 0)");
        });
        var scene = model.Entity<MovieScene>(); scene.ToTable("MovieScenes", "video"); scene.HasKey(x => new { x.AnalysisId, x.Ordinal });
        scene.HasOne<SceneAnalysis>().WithMany().HasForeignKey(x => x.AnalysisId).OnDelete(DeleteBehavior.Restrict);
        scene.ToTable(t => t.HasCheckConstraint("CK_Scene_Range", "\"Ordinal\" >= 0 AND \"StartFrame\" >= 0 AND \"EndFrame\" > \"StartFrame\" AND \"StartSeconds\" >= 0 AND \"EndSeconds\" > \"StartSeconds\""));
        var audio = model.Entity<AudioExtraction>(); audio.ToTable("AudioExtractions", "video"); audio.HasKey(x => x.Id); audio.Ignore(x => x.DurationSeconds);
        audio.Property(x => x.SourceSha256).HasMaxLength(64); audio.Property(x => x.ArtifactSha256).HasMaxLength(64); audio.Property(x => x.FfmpegVersion).HasMaxLength(256);
        audio.Property(x => x.IdempotencyKey).HasMaxLength(128); audio.Property(x => x.RequestedBy).HasMaxLength(256); audio.Property(x => x.Profile).HasMaxLength(64);
        audio.HasIndex(x => new { x.ImportId, x.IdempotencyKey }).IsUnique(); audio.HasIndex(x => x.JobId).IsUnique(); audio.HasIndex(x => new { x.ImportId, x.CreatedAt });
        audio.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Audio_Binding", "\"AudioIndex\" >= 0 AND \"SourceSizeBytes\" > 0 AND \"SourceSha256\" ~ '^[0-9a-f]{64}$' AND \"MaxAttempts\" BETWEEN 1 AND 5 AND \"Profile\" = 'wav-pcm-s16le-16000-mono-v1'");
            t.HasCheckConstraint("CK_Audio_Completion", "(\"CompletedAt\" IS NULL AND \"ArtifactToken\" IS NULL AND \"ArtifactSha256\" IS NULL AND \"ArtifactSizeBytes\" IS NULL AND \"Samples\" IS NULL AND \"FfmpegVersion\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"ArtifactToken\" IS NOT NULL AND \"ArtifactSha256\" IS NOT NULL AND \"ArtifactSha256\" ~ '^[0-9a-f]{64}$' AND \"Samples\" IS NOT NULL AND \"Samples\" > 0 AND \"Samples\" <= 2147483629 AND \"ArtifactSizeBytes\" IS NOT NULL AND \"ArtifactSizeBytes\" = 44 + 2 * \"Samples\" AND \"FfmpegVersion\" IS NOT NULL)");
        });
        var sampling = model.Entity<FrameSampling>(); sampling.ToTable("FrameSamplings", "video"); sampling.HasKey(x => x.Id);
        sampling.Property(x => x.IdempotencyKey).HasMaxLength(128); sampling.Property(x => x.RequestedBy).HasMaxLength(256); sampling.Property(x => x.Profile).HasMaxLength(64); sampling.Property(x => x.FfmpegVersion).HasMaxLength(256);
        sampling.HasIndex(x => new { x.SceneAnalysisId, x.IdempotencyKey }).IsUnique(); sampling.HasIndex(x => x.JobId).IsUnique(); sampling.HasIndex(x => new { x.SceneAnalysisId, x.CreatedAt });
        sampling.HasOne<SceneAnalysis>().WithMany().HasForeignKey(x => x.SceneAnalysisId).OnDelete(DeleteBehavior.Restrict);
        sampling.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Sampling_Settings", "\"SamplesPerScene\" BETWEEN 1 AND 3 AND \"MaxAttempts\" BETWEEN 1 AND 5 AND \"Profile\" = 'png-rgb24-320x180-frame-quantiles-v1'");
            t.HasCheckConstraint("CK_Sampling_Completion", "(\"CompletedAt\" IS NULL AND \"ArtifactToken\" IS NULL AND \"FfmpegVersion\" IS NULL) OR (\"CompletedAt\" IS NOT NULL AND \"ArtifactToken\" IS NOT NULL AND \"FfmpegVersion\" IS NOT NULL)");
        });
        var sample = model.Entity<FrameSample>(); sample.ToTable("FrameSamples", "video"); sample.HasKey(x => new { x.SamplingId, x.Ordinal }); sample.Ignore(x => x.Plan); sample.Property(x => x.Sha256).HasMaxLength(64);
        sample.HasOne<FrameSampling>().WithMany().HasForeignKey(x => x.SamplingId).OnDelete(DeleteBehavior.Restrict);
        sample.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Sample_Plan", "\"Ordinal\" BETWEEN 0 AND 299 AND \"SceneOrdinal\" >= 0 AND \"FrameIndex\" >= 0 AND \"StartSeconds\" >= 0 AND \"EndSeconds\" > \"StartSeconds\"");
            t.HasCheckConstraint("CK_Sample_Result", "(\"SourceSeconds\" IS NULL AND \"NormalizedSeconds\" IS NULL AND \"Sha256\" IS NULL AND \"SizeBytes\" IS NULL) OR (\"SourceSeconds\" IS NOT NULL AND \"NormalizedSeconds\" IS NOT NULL AND \"NormalizedSeconds\" >= \"StartSeconds\" - 0.000001 AND \"NormalizedSeconds\" < \"EndSeconds\" AND \"Sha256\" IS NOT NULL AND \"Sha256\" ~ '^[0-9a-f]{64}$' AND \"SizeBytes\" IS NOT NULL AND \"SizeBytes\" BETWEEN 1 AND 4194304)");
        });
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


namespace MovieRecap.Jobs.Application;

// Used by infrastructure composition inside an existing local database transaction.
// Participating modules depend only on this contract, never on Jobs tables.
public interface IJobTransaction
{
    Task<JobView> CreateAsync(CreateJob command, CancellationToken ct);
    Task<JobView?> GetAsync(Guid id, CancellationToken ct);
    Task CompleteAsync(JobLease lease, CancellationToken ct);
    Task RequireLeaseAsync(JobLease lease, CancellationToken ct);
}

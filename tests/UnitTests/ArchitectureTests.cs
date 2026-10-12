using MovieRecap.Jobs.Application;
using MovieRecap.Jobs.Domain;
using Xunit;
namespace MovieRecap.UnitTests;

public class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_framework_or_infrastructure_dependencies()
    {
        var refs = typeof(ProcessingJob).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(refs, x => x.StartsWith("Microsoft.") || x.StartsWith("Npgsql") || x.StartsWith("MovieRecap."));
    }
    [Fact]
    public void Application_does_not_reference_infrastructure_or_hosts()
    {
        var refs = typeof(JobService).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain(refs, x => x.Contains("Infrastructure") || x.Contains("Api") || x.Contains("Worker") || x.Contains("EntityFramework"));
    }
}


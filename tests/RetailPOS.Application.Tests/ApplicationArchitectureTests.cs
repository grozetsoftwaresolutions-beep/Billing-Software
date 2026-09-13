using Xunit;

namespace RetailPOS.Application.Tests;

public class ApplicationArchitectureTests
{
    [Fact]
    public void ApplicationAssembly_ShouldNotReferenceInfrastructureOrServer()
    {
        var referencedAssemblies = typeof(AssemblyReference).Assembly.GetReferencedAssemblies();
        
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Server", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Client", StringComparison.OrdinalIgnoreCase));
    }
}

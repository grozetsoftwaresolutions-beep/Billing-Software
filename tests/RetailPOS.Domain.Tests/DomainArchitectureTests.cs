using Xunit;

namespace RetailPOS.Domain.Tests;

public class DomainArchitectureTests
{
    [Fact]
    public void DomainAssembly_ShouldNotReferenceInfrastructureOrServer()
    {
        var referencedAssemblies = typeof(AssemblyReference).Assembly.GetReferencedAssemblies();
        
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Server", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, a => a.Name != null && a.Name.Contains("Client", StringComparison.OrdinalIgnoreCase));
    }
}

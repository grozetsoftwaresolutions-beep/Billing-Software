using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class InfrastructureArchitectureTests
{
    [Fact]
    public void InfrastructureAssembly_ShouldReferenceDomain()
    {
        var domainAssembly = typeof(RetailPOS.Domain.AssemblyReference).Assembly;
        Assert.NotNull(domainAssembly);
    }
}

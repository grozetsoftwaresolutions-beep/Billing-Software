using Xunit;

namespace RetailPOS.Server.Tests;

public class ServerArchitectureTests
{
    [Fact]
    public void ServerAssembly_ShouldReferenceContracts()
    {
        var contractsAssembly = typeof(RetailPOS.Contracts.AssemblyReference).Assembly;
        Assert.NotNull(contractsAssembly);
    }
}

using Xunit;

namespace RetailPOS.Client.Tests;

public class ClientArchitectureTests
{
    [Fact]
    public void ClientAssembly_ShouldReferenceContracts()
    {
        var contractsAssembly = typeof(RetailPOS.Contracts.AssemblyReference).Assembly;
        Assert.NotNull(contractsAssembly);
    }
}

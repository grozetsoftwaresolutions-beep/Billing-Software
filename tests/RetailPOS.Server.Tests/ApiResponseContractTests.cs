using System.Text.Json;
using RetailPOS.Contracts.Common;
using Xunit;

namespace RetailPOS.Server.Tests;

public sealed class ApiResponseContractTests
{
    private sealed record SampleData(string Name, int Quantity);

    [Fact]
    public void ApiResponse_Ok_PopulatesStandardEnvelope()
    {
        var correlationId = "corr-12345";
        var payload = new SampleData("Wheat Flour", 5);

        var response = ApiResponse.Ok(payload, correlationId);

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("Wheat Flour", response.Data.Name);
        Assert.Equal(5, response.Data.Quantity);
        Assert.Equal(correlationId, response.CorrelationId);
        Assert.Null(response.ErrorCode);
        Assert.Null(response.ErrorMessage);
    }

    [Fact]
    public void ApiResponse_Fail_PopulatesStandardEnvelope()
    {
        var correlationId = "corr-fail-999";
        var errorMsg = "Insufficient inventory stock.";
        var errorCode = "INSUFFICIENT_STOCK";

        var response = ApiResponse.Fail<SampleData>(errorMsg, errorCode, correlationId);

        Assert.False(response.Success);
        Assert.Null(response.Data);
        Assert.Equal(errorMsg, response.ErrorMessage);
        Assert.Equal(errorCode, response.ErrorCode);
        Assert.Equal(correlationId, response.CorrelationId);
    }

    [Fact]
    public void ApiResponse_JsonRoundtrip_PreservesContract()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var correlationId = "corr-json-777";
        var original = ApiResponse.Ok(new SampleData("Basmati Rice", 10), correlationId);

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ApiResponse<SampleData>>(json, options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.NotNull(deserialized.Data);
        Assert.Equal("Basmati Rice", deserialized.Data.Name);
        Assert.Equal(correlationId, deserialized.CorrelationId);
    }
}

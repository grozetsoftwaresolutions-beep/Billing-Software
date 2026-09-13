using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Converters;

public sealed class UnitOfMeasureToStringConverter : ValueConverter<UnitOfMeasure, string>
{
    public UnitOfMeasureToStringConverter()
        : base(
            u => u.Code,
            code => UnitOfMeasure.FromCode(code))
    {
    }
}

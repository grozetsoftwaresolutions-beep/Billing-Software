using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Infrastructure.Persistence.Converters;

public sealed class BarcodeToStringConverter : ValueConverter<Barcode, string>
{
    public BarcodeToStringConverter()
        : base(
            barcode => barcode.Value,
            value => new Barcode(value))
    {
    }
}

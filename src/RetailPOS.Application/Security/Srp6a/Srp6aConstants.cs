using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Math;

namespace RetailPOS.Application.Security.Srp6a;

public static class Srp6aConstants
{
    // RFC 5054 / RFC 3526 2048-bit MODP Group 14
    public static readonly BigInteger N = Srp6StandardGroups.rfc5054_2048.N;
    public static readonly BigInteger G = Srp6StandardGroups.rfc5054_2048.G;
    public const int GroupByteLength = 256;
    public const int HashByteLength = 32;
    public const int SaltByteLength = 16;
}

using System;
using System.Security.Cryptography;

namespace RetailPOS.Application.Security;

public interface IServerIdentityKeyService : IDisposable
{
    byte[] GetPublicKeySpki();
    byte[] SignData(byte[] data);
    bool VerifyData(byte[] data, byte[] signature);
    bool IsExportable();
}

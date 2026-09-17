using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RetailPOS.Application.Security;
using RetailPOS.Domain.Common;

namespace RetailPOS.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsCngServerIdentityService : IServerIdentityKeyService
{
    private readonly string _keyName;
    private readonly bool _useMachineKey;
    private CngKey? _cngKey;
    private ECDsaCng? _ecdsa;
    private bool _disposed;

    public WindowsCngServerIdentityService(string keyName = "RetailPOS_Server_Root_Key", bool useMachineKey = false, bool createIfMissing = true)
    {
        _keyName = keyName;
        _useMachineKey = useMachineKey;
        InitializeKey(createIfMissing);
    }

    private void InitializeKey(bool createIfMissing)
    {
        var openOptions = _useMachineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

        if (CngKey.Exists(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions))
        {
            _cngKey = CngKey.Open(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, openOptions);
        }
        else
        {
            if (!createIfMissing)
            {
                throw new DomainException($"Root identity key '{_keyName}' is missing. Failing closed with Exit Code 110.", "ERROR_ROOT_IDENTITY_MISSING");
            }

            var creationOptions = _useMachineKey 
                ? (CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey)
                : CngKeyCreationOptions.OverwriteExistingKey;

            var parameters = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = creationOptions
            };

            _cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, _keyName, parameters);
        }

        _ecdsa = new ECDsaCng(_cngKey);
    }

    public byte[] GetPublicKeySpki()
    {
        EnsureNotDisposed();
        return _ecdsa!.ExportSubjectPublicKeyInfo();
    }

    public byte[] SignData(byte[] data)
    {
        EnsureNotDisposed();
        return _ecdsa!.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool VerifyData(byte[] data, byte[] signature)
    {
        EnsureNotDisposed();
        return _ecdsa!.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool IsExportable()
    {
        EnsureNotDisposed();
        return (_cngKey!.ExportPolicy & CngExportPolicies.AllowExport) != 0 ||
               (_cngKey.ExportPolicy & CngExportPolicies.AllowPlaintextExport) != 0;
    }

    public void DeleteKey()
    {
        EnsureNotDisposed();
        _ecdsa?.Dispose();
        _ecdsa = null;
        _cngKey?.Delete();
        _cngKey = null;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ecdsa?.Dispose();
            _cngKey?.Dispose();
            _disposed = true;
        }
    }
}

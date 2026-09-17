using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using RetailPOS.Contracts.Terminals;

namespace RetailPOS.Application.Security.Srp6a;

public static class Srp6aApplicationProfile
{
    private static readonly BigInteger N = Srp6aConstants.N;
    private static readonly BigInteger G = Srp6aConstants.G;

    public static byte[] CalculateMultiplierK()
    {
        byte[] nBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, N);
        byte[] gBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, G);
        byte[] buffer = new byte[Srp6aConstants.GroupByteLength * 2];
        Buffer.BlockCopy(nBytes, 0, buffer, 0, Srp6aConstants.GroupByteLength);
        Buffer.BlockCopy(gBytes, 0, buffer, Srp6aConstants.GroupByteLength, Srp6aConstants.GroupByteLength);
        return SHA256.HashData(buffer);
    }

    public static BigInteger MultiplierK => new BigInteger(1, CalculateMultiplierK());

    public static byte[] CalculateIdentityBytes(Guid storeId, Guid terminalId)
    {
        string idStr = $"{storeId:D}:{terminalId:D}".ToLowerInvariant();
        return Encoding.UTF8.GetBytes(idStr);
    }

    public static byte[] NormalizePasscodeBytes(string humanPairingCode)
    {
        string norm = CrockfordBase32.Normalize(humanPairingCode);
        return Encoding.UTF8.GetBytes(norm);
    }

    public static BigInteger CalculateX(byte[] salt, byte[] identityBytes, byte[] passcodeBytes)
    {
        byte[] colon = Encoding.UTF8.GetBytes(":");
        byte[] innerInput = new byte[identityBytes.Length + 1 + passcodeBytes.Length];
        Buffer.BlockCopy(identityBytes, 0, innerInput, 0, identityBytes.Length);
        Buffer.BlockCopy(colon, 0, innerInput, identityBytes.Length, 1);
        Buffer.BlockCopy(passcodeBytes, 0, innerInput, identityBytes.Length + 1, passcodeBytes.Length);

        byte[] innerHash = SHA256.HashData(innerInput);

        byte[] outerInput = new byte[salt.Length + innerHash.Length];
        Buffer.BlockCopy(salt, 0, outerInput, 0, salt.Length);
        Buffer.BlockCopy(innerHash, 0, outerInput, salt.Length, innerHash.Length);

        byte[] xBytes = SHA256.HashData(outerInput);
        try
        {
            return new BigInteger(1, xBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerInput);
            CryptographicOperations.ZeroMemory(innerHash);
            CryptographicOperations.ZeroMemory(outerInput);
            CryptographicOperations.ZeroMemory(xBytes);
        }
    }

    public static BigInteger CalculateVerifier(BigInteger privateX)
    {
        return G.ModPow(privateX, N);
    }

    public static BigInteger GenerateEphemeralScalar(SecureRandom? random = null)
    {
        random ??= new SecureRandom();
        BigInteger scalar;
        do
        {
            scalar = new BigInteger(256, random);
        }
        while (scalar.CompareTo(BigInteger.One) < 0 || scalar.CompareTo(N.Subtract(BigInteger.One)) >= 0);
        return scalar;
    }

    public static BigInteger CalculateClientEphemeralA(BigInteger clientSecretA)
    {
        BigInteger clientPublicA = G.ModPow(clientSecretA, N);
        if (clientPublicA.Mod(N).Equals(BigInteger.Zero))
            throw new CryptographicException("Invalid client ephemeral A: A mod N == 0");
        return clientPublicA;
    }

    public static BigInteger CalculateServerEphemeralB(BigInteger verifierV, BigInteger serverSecretB)
    {
        BigInteger k = MultiplierK;
        BigInteger gb = G.ModPow(serverSecretB, N);
        BigInteger kv = k.Multiply(verifierV).Mod(N);
        BigInteger serverPublicB = kv.Add(gb).Mod(N);
        if (serverPublicB.Mod(N).Equals(BigInteger.Zero))
            throw new CryptographicException("Invalid server ephemeral B: B mod N == 0");
        return serverPublicB;
    }

    public static BigInteger CalculateScramblerU(BigInteger clientPublicA, BigInteger serverPublicB)
    {
        byte[] aPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, clientPublicA);
        byte[] bPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, serverPublicB);
        byte[] buffer = new byte[Srp6aConstants.GroupByteLength * 2];
        Buffer.BlockCopy(aPad, 0, buffer, 0, Srp6aConstants.GroupByteLength);
        Buffer.BlockCopy(bPad, 0, buffer, Srp6aConstants.GroupByteLength, Srp6aConstants.GroupByteLength);

        byte[] uBytes = SHA256.HashData(buffer);
        BigInteger u = new BigInteger(1, uBytes);
        if (u.Equals(BigInteger.Zero))
            throw new CryptographicException("Invalid scrambler parameter u: u == 0");
        return u;
    }

    public static BigInteger CalculateClientSessionSecret(
        BigInteger clientPublicA,
        BigInteger serverPublicB,
        BigInteger clientSecretA,
        BigInteger privateX)
    {
        if (clientPublicA.Mod(N).Equals(BigInteger.Zero) || serverPublicB.Mod(N).Equals(BigInteger.Zero))
            throw new CryptographicException("Illegal public ephemeral: A or B mod N == 0");

        BigInteger u = CalculateScramblerU(clientPublicA, serverPublicB);
        BigInteger k = MultiplierK;
        BigInteger gx = G.ModPow(privateX, N);
        BigInteger kgx = k.Multiply(gx).Mod(N);
        BigInteger diff = serverPublicB.Subtract(kgx).Mod(N);
        if (diff.SignValue < 0) diff = diff.Add(N);

        BigInteger exp = clientSecretA.Add(u.Multiply(privateX));
        return diff.ModPow(exp, N);
    }

    public static BigInteger CalculateServerSessionSecret(
        BigInteger clientPublicA,
        BigInteger serverPublicB,
        BigInteger verifierV,
        BigInteger serverSecretB)
    {
        if (clientPublicA.Mod(N).Equals(BigInteger.Zero) || serverPublicB.Mod(N).Equals(BigInteger.Zero))
            throw new CryptographicException("Illegal public ephemeral: A or B mod N == 0");

        BigInteger u = CalculateScramblerU(clientPublicA, serverPublicB);
        BigInteger vu = verifierV.ModPow(u, N);
        BigInteger avu = clientPublicA.Multiply(vu).Mod(N);
        return avu.ModPow(serverSecretB, N);
    }

    public static byte[] CalculateSharedSessionKeyKpake(BigInteger S)
    {
        byte[] sPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, S);
        try
        {
            return SHA256.HashData(sPad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sPad);
        }
    }

    public static byte[] CalculateClientEvidenceM1(
        BigInteger clientPublicA,
        BigInteger serverPublicB,
        byte[] identityBytes,
        byte[] salt,
        byte[] kPake)
    {
        byte[] nBytes = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, N);
        byte[] gBytes = BigIntegers.AsUnsignedByteArray(G);
        byte[] hn = SHA256.HashData(nBytes);
        byte[] hg = SHA256.HashData(gBytes);
        byte[] xorH = new byte[32];
        for (int i = 0; i < 32; i++) xorH[i] = (byte)(hn[i] ^ hg[i]);

        byte[] hi = SHA256.HashData(identityBytes);
        byte[] aPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, clientPublicA);
        byte[] bPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, serverPublicB);

        byte[] m1Input = new byte[32 + 32 + salt.Length + Srp6aConstants.GroupByteLength * 2 + kPake.Length];
        int off = 0;
        Buffer.BlockCopy(xorH, 0, m1Input, off, 32); off += 32;
        Buffer.BlockCopy(hi, 0, m1Input, off, 32); off += 32;
        Buffer.BlockCopy(salt, 0, m1Input, off, salt.Length); off += salt.Length;
        Buffer.BlockCopy(aPad, 0, m1Input, off, Srp6aConstants.GroupByteLength); off += Srp6aConstants.GroupByteLength;
        Buffer.BlockCopy(bPad, 0, m1Input, off, Srp6aConstants.GroupByteLength); off += Srp6aConstants.GroupByteLength;
        Buffer.BlockCopy(kPake, 0, m1Input, off, kPake.Length);

        return SHA256.HashData(m1Input);
    }

    public static byte[] CalculateServerEvidenceM2(BigInteger clientPublicA, byte[] clientEvidenceM1, byte[] kPake)
    {
        byte[] aPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, clientPublicA);
        byte[] m2Input = new byte[Srp6aConstants.GroupByteLength + clientEvidenceM1.Length + kPake.Length];
        Buffer.BlockCopy(aPad, 0, m2Input, 0, Srp6aConstants.GroupByteLength);
        Buffer.BlockCopy(clientEvidenceM1, 0, m2Input, Srp6aConstants.GroupByteLength, clientEvidenceM1.Length);
        Buffer.BlockCopy(kPake, 0, m2Input, Srp6aConstants.GroupByteLength + clientEvidenceM1.Length, kPake.Length);

        return SHA256.HashData(m2Input);
    }

    public static string CalculateSasConfirmationCode(
        byte[] kPake,
        Guid storeId,
        Guid terminalId,
        Guid pairingOpId,
        byte[] m1,
        byte[] m2)
    {
        string sasHeader = "RetailPOS-SAS-v1.0\n";
        string m1B64 = Convert.ToBase64String(m1);
        string m2B64 = Convert.ToBase64String(m2);
        string payload = $"{sasHeader}{storeId:D}\n{terminalId:D}\n{pairingOpId:D}\n{m1B64}\n{m2B64}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(kPake);
        byte[] hmacBytes = hmac.ComputeHash(payloadBytes);

        // First 5 bytes (40 bits) = 8 Crockford Base32 characters formatted as XXXX-XXXX
        byte[] fiveBytes = new byte[5];
        Buffer.BlockCopy(hmacBytes, 0, fiveBytes, 0, 5);
        string encoded = CrockfordBase32.Encode(fiveBytes);
        return CrockfordBase32.FormatGroups(encoded, 4);
    }

    public static byte[] DeriveKeyWrap(byte[] kPake, BigInteger clientPublicA, BigInteger serverPublicB)
    {
        byte[] aPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, clientPublicA);
        byte[] bPad = BigIntegers.AsUnsignedByteArray(Srp6aConstants.GroupByteLength, serverPublicB);
        string aB64 = Convert.ToBase64String(aPad);
        string bB64 = Convert.ToBase64String(bPad);
        string infoWrap = $"RetailPOS-TMK-Wrap-v1.0\n{aB64}\n{bB64}";
        byte[] infoBytes = Encoding.UTF8.GetBytes(infoWrap);

        return HKDF.Expand(HashAlgorithmName.SHA256, kPake, 32, infoBytes);
    }

    public static EncryptedTmkDto WrapTmk(byte[] kWrap, byte[] tmkBytes, Guid storeId, Guid terminalId)
    {
        if (kWrap.Length != 32) throw new ArgumentException("K_wrap must be exactly 32 bytes.", nameof(kWrap));
        if (tmkBytes.Length != 32) throw new ArgumentException("TMK must be exactly 32 bytes.", nameof(tmkBytes));

        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[tmkBytes.Length];

        string aadStr = $"{storeId:D}\n{terminalId:D}";
        byte[] aad = Encoding.UTF8.GetBytes(aadStr);

        using var aesGcm = new AesGcm(kWrap, 16);
        aesGcm.Encrypt(iv, tmkBytes, ciphertext, tag, aad);

        return new EncryptedTmkDto(
            Convert.ToBase64String(iv),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(aad));
    }

    public static byte[] UnwrapTmk(byte[] kWrap, EncryptedTmkDto encryptedTmk, Guid storeId, Guid terminalId)
    {
        if (kWrap.Length != 32) throw new ArgumentException("K_wrap must be exactly 32 bytes.", nameof(kWrap));

        byte[] iv = Convert.FromBase64String(encryptedTmk.IvBase64);
        byte[] tag = Convert.FromBase64String(encryptedTmk.TagBase64);
        byte[] ciphertext = Convert.FromBase64String(encryptedTmk.CiphertextBase64);
        byte[] expectedAad = Encoding.UTF8.GetBytes($"{storeId:D}\n{terminalId:D}");

        byte[] actualAad = Convert.FromBase64String(encryptedTmk.AadBase64);
        if (!CryptographicOperations.FixedTimeEquals(expectedAad, actualAad))
            throw new CryptographicException("AAD mismatch on TMK unwrapping. Terminal binding violated.");

        byte[] plaintextTmk = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(kWrap, 16);
        aesGcm.Decrypt(iv, ciphertext, tag, plaintextTmk, actualAad);

        return plaintextTmk;
    }

    public static byte[] DeriveRetryAuthToken(byte[] kPake, Guid storeId, Guid terminalId, Guid pairingOpId, byte[] m1)
    {
        string retryPayload = $"RetailPOS-Pairing-RetryToken-v1.0\n{storeId:D}\n{terminalId:D}\n{pairingOpId:D}\n{Convert.ToBase64String(m1)}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(retryPayload);

        using var hmac = new HMACSHA256(kPake);
        return hmac.ComputeHash(payloadBytes);
    }

    public static byte[] CalculateRetryAuthHash(byte[] retryAuthToken)
    {
        return SHA256.HashData(retryAuthToken);
    }

    public static byte[] DeriveFinalizeProof(byte[] kPake, Guid pairingOpId, byte[] m2)
    {
        string m2B64 = Convert.ToBase64String(m2);
        string payload = $"RetailPOS-Pairing-Finalize-v1.0\n{pairingOpId:D}\n{m2B64}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(kPake);
        return hmac.ComputeHash(payloadBytes);
    }

    public static bool VerifyFinalizeProof(byte[] kPake, Guid pairingOpId, byte[] m2, byte[] candidateProof)
    {
        byte[] expectedProof = DeriveFinalizeProof(kPake, pairingOpId, m2);
        return CryptographicOperations.FixedTimeEquals(expectedProof, candidateProof);
    }
}

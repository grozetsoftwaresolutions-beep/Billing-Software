using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RetailPOS.Application.Security;

[SupportedOSPlatform("windows")]
public static class DpapiStorageHelper
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public static byte[] Protect(byte[] plaintext, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, null, scope);
    }

    public static byte[] Unprotect(byte[] ciphertext, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, null, scope);
    }

    public static void SaveProtectedJson<T>(string filePath, T data, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        string json = JsonSerializer.Serialize(data, IndentedOptions);
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = Protect(plaintextBytes, scope);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(filePath, encryptedBytes);
    }

    public static T? LoadProtectedJson<T>(string filePath, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (!File.Exists(filePath))
            return default;

        byte[] encryptedBytes = File.ReadAllBytes(filePath);
        byte[] plaintextBytes = Unprotect(encryptedBytes, scope);
        string json = Encoding.UTF8.GetString(plaintextBytes);
        return JsonSerializer.Deserialize<T>(json);
    }

    public static HashSet<Guid> LoadTombstoneStore(string tombstoneFilePath)
    {
        if (!File.Exists(tombstoneFilePath))
            return [];

        try
        {
            var list = LoadProtectedJson<List<Guid>>(tombstoneFilePath, DataProtectionScope.CurrentUser);
            return list != null ? new HashSet<Guid>(list) : [];
        }
        catch
        {
            return [];
        }
    }

    public static void RecordTombstone(string tombstoneFilePath, Guid rotationOpId)
    {
        var existing = LoadTombstoneStore(tombstoneFilePath);
        existing.Add(rotationOpId);
        SaveProtectedJson(tombstoneFilePath, new List<Guid>(existing), DataProtectionScope.CurrentUser);
    }

    public static bool IsTombstoneRecorded(string tombstoneFilePath, Guid rotationOpId)
    {
        var existing = LoadTombstoneStore(tombstoneFilePath);
        return existing.Contains(rotationOpId);
    }
}

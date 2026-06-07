using System.Security.Cryptography;

namespace Tachion.Core;

public static class HashUtil
{
    public static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}

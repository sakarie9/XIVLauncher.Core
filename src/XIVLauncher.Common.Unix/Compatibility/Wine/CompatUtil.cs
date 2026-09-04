using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace XIVLauncher.Common.Unix.Compatibility.Wine;

public static class CompatUtil
{
    public static bool EnsureChecksumMatch(string filePath, string[] checksums)
    {
        if (checksums.Length == 0)
            return false;

        if (checksums.Any(checksum => string.Equals(checksum, "skip")))
            return true;

        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        var computedHash = Convert.ToHexString(sha512.ComputeHash(stream)).ToLowerInvariant();
        return checksums.Any(checksum => string.Equals(checksum, computedHash, StringComparison.OrdinalIgnoreCase));
    }
}

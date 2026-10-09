/*
    Copyright 2026 Artem Drobanov (artem.drobanov@gmail.com)
    Licensed under the Apache License, Version 2.0 (the "License");
    you may Not use this file except In compliance With the License.
    You may obtain a copy Of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law Or agreed To In writing, software
    distributed under the License is distributed on An "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions And
    limitations under the License.
*/

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

public static class Sha256Secure // 997ecdb10c9f957e7c1bc29255084b9c1f156d4ee9b36e943711751460b87dda
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] HashData(byte[] data, string? secret) => HashData(data.AsSpan(), secret);

    public static byte[] HashData(ReadOnlySpan<byte> data, string? secret)
    {
        byte[] hash = new byte[32];
        HashData(data, secret, hash);
        return hash;
    }

    public static void HashData(ReadOnlySpan<byte> data, string? secret, Span<byte> hash)
    {
        if (hash.Length < 32)
            throw new ArgumentException(nameof(hash));
        if (string.IsNullOrEmpty(secret))
        {
            Sha256Compact.HashData(data, hash);
            return;
        }
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[]? rented = null;
        int total = secretBytes.Length + data.Length;
        Span<byte> input = total <= 4096
            ? stackalloc byte[4096]
            : rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            secretBytes.CopyTo(input);
            data.CopyTo(input[secretBytes.Length..]);
            Sha256Compact.HashData(input[..total], hash);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool Test()
    {
        if (!Sha256Compact.Test())
            return false;
        byte[] secretBytes = Encoding.UTF8.GetBytes("secret");
        byte[] data = [0x61, 0x62, 0x63];
        byte[] concat = [.. secretBytes, .. data];
        Span<byte> salted = stackalloc byte[32];
        Span<byte> expected = stackalloc byte[32];
        HashData(data, "secret", salted);
        Sha256Compact.HashData(concat, expected);
        return salted.SequenceEqual(expected);
    }
}
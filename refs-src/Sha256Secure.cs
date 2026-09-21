/*
    Copyright 2026 Artem Drobanov (artem.drobanov@gmail.com)
    Licensed under the Apache License, Version 2.0 (the "License");
    you may Not use this file except In compliance With the License.
    You may obtain a copy Of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law Or agreed To In writing, software
    distributed under the License Is distributed On An "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS Of ANY KIND, either express Or implied.
    See the License For the specific language governing permissions And
    limitations under the License.
*/

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

// SHA-256 с режимом «секрет»: секрет — байтовый префикс потока в каждом вызове
// (аналог HMAC на базе стандартного хеша). Без HashSecret идентичен Sha256Compact.
public static class Sha256Secure
{
    private sealed class SecretScope
    {
        public readonly string Text;
        public readonly byte[]? Bytes;

        public SecretScope(string text, byte[]? bytes)
        {
            Text = text;
            Bytes = bytes;
        }
    }

    private static readonly AsyncLocal<SecretScope?> Scope = new();

    public static string? HashSecret
    {
        get => Scope.Value?.Text;
        set => Scope.Value = string.IsNullOrEmpty(value)
            ? null
            : new SecretScope(value, Encoding.UTF8.GetBytes(value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] HashData(byte[] data) => HashData(data.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] hash = new byte[32]; HashData(data, hash); return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HashData(ReadOnlySpan<byte> data, Span<byte> hash)
    {
        if (hash.Length < 32) throw new ArgumentException(nameof(hash));
        var secret = Scope.Value?.Bytes;
        if (secret is null || secret.Length == 0) { Sha256Compact.HashData(data, hash); return; }
        byte[]? rented = null;
        var total = secret.Length + data.Length;
        Span<byte> input = total <= 4096
            ? stackalloc byte[4096]
            : rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            secret.CopyTo(input); data.CopyTo(input[secret.Length..]);
            Sha256Compact.HashData(input[..total], hash);
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Test()
    {
        if (!Sha256Compact.Test()) return false;
        var secret = Scope.Value?.Bytes;
        if (secret is null || secret.Length == 0) return true;
        var concat = new byte[secret.Length + 3];
        secret.CopyTo(concat, 0);
        concat[^3] = 0x61; concat[^2] = 0x62; concat[^1] = 0x63;
        Span<byte> h1 = stackalloc byte[32]; Span<byte> h2 = stackalloc byte[32];
        HashData(concat.AsSpan(secret.Length), h1);
        Sha256Compact.HashData(concat, h2);
        return h1.SequenceEqual(h2);
    }
}
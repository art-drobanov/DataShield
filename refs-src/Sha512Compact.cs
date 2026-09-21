/*
    Copyright 2026 Artem Drobanov (artem.drobanov@gmail.com)
    Licensed under the Apache License, Version 2.0 (the "License");
    you may Not use this file except In compliance With the License.
    You may obtain a copy Of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law Or agreed To In writing, software
    distributed under the License Is distributed On an "AS IS" BASIS,
    WITHOUT WARRANTIES Or CONDITIONS Of ANY KIND, either express Or implied.
    See the License For the specific language governing permissions And
    limitations under the License.
*/

using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

public static class Sha512Compact // 16341c7c78e68831b4315610297521aaa6700d0f18136680371f57e0c9cef397
{
    private static readonly byte[] K = Convert.FromHexString(
    "428a2f98d728ae227137449123ef65cdb5c0fbcfec4d3b2fe9b5dba58189dbbc" +
    "3956c25bf348b53859f111f1b605d019923f82a4af194f9bab1c5ed5da6d8118" +
    "d807aa98a303024212835b0145706fbe243185be4ee4b28c550c7dc3d5ffb4e2" +
    "72be5d74f27b896f80deb1fe3b1696b19bdc06a725c71235c19bf174cf692694" +
    "e49b69c19ef14ad2efbe4786384f25e30fc19dc68b8cd5b5240ca1cc77ac9c65" +
    "2de92c6f592b02754a7484aa6ea6e4835cb0a9dcbd41fbd476f988da831153b5" +
    "983e5152ee66dfaba831c66d2db43210b00327c898fb213fbf597fc7beef0ee4" +
    "c6e00bf33da88fc2d5a79147930aa72506ca6351e003826f142929670a0e6e70" +
    "27b70a8546d22ffc2e1b21385c26c9264d2c6dfc5ac42aed53380d139d95b3df" +
    "650a73548baf63de766a0abb3c77b2a881c2c92e47edaee692722c851482353b" +
    "a2bfe8a14cf10364a81a664bbc423001c24b8b70d0f89791c76c51a30654be30" +
    "d192e819d6ef5218d69906245565a910f40e35855771202a106aa07032bbd1b8" +
    "19a4c116b8d2d0c81e376c085141ab532748774cdf8eeb9934b0bcb5e19b48a8" +
    "391c0cb3c5c95a634ed8aa4ae3418acb5b9cca4f7763e373682e6ff3d6b2b8a3" +
    "748f82ee5defb2fc78a5636f43172f6084c87814a1f0ab728cc702081a6439ec" +
    "90befffa23631e28a4506cebde82bde9bef9a3f7b2c67915c67178f2e372532b" +
    "ca273eceea26619cd186b8c721c0c207eada7dd6cde0eb1ef57d4f7fee6ed178" +
    "06f067aa72176fba0a637dc5a2c898a6113f9804bef90dae1b710b35131c471b" +
    "28db77f523047d8432caab7b40c724933c9ebe0a15c9bebc431d67c49c100d4c" +
    "4cc5d4becb3e42b6597f299cfc657e2a5fcb6fab3ad6faec6c44198c4a475817");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong R(ulong x, int n) => BitOperations.RotateRight(x, n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong S0(ulong x) => R(x, 28) ^ R(x, 34) ^ R(x, 39);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong S1(ulong x) => R(x, 14) ^ R(x, 18) ^ R(x, 41);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong G0(ulong x) => R(x, 01) ^ R(x, 08) ^ x >> 07;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong G1(ulong x) => R(x, 19) ^ R(x, 61) ^ x >> 06;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] HashData(byte[] data) => HashData(data.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] hash = new byte[64]; HashData(data, hash); return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HashData(ReadOnlySpan<byte> data, Span<byte> hash)
    {
        if (hash.Length < 64) throw new ArgumentException(nameof(hash));
        int length = data.Length;
        Span<ulong> s = [0x6A09E667F3BCC908, 0xBB67AE8584CAA73B, 0x3C6EF372FE94F82B, 0xA54FF53A5F1D36F1,
                         0x510E527FADE682D1, 0x9B05688C2B3E6C1F, 0x1F83D9ABFB41BD6B, 0x5BE0CD19137E2179];
        while (data.Length >= 128) { Compress(data[..128], s); data = data[128..]; }
        Span<byte> tail = stackalloc byte[256];
        tail.Clear(); data.CopyTo(tail); tail[data.Length] = 0x80;
        int padded = data.Length < 112 ? 128 : 256;
        BinaryPrimitives.WriteUInt64BigEndian(tail.Slice(padded - 16), (ulong)length >> 61);
        BinaryPrimitives.WriteUInt64BigEndian(tail.Slice(padded - 8), (ulong)length << 3);
        Compress(tail[..128], s); if (padded == 256) Compress(tail[128..], s);
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64BigEndian(hash.Slice(i * 8), s[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Test() =>
        Convert.ToHexString(HashData(new byte[] { 0x61, 0x62, 0x63 })) ==
        "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A" +
        "2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Compress(ReadOnlySpan<byte> block, Span<ulong> s)
    {
        unchecked
        {
            Span<ulong> w = stackalloc ulong[16];
            ulong a = s[0], b = s[1], c = s[2], d = s[3];
            ulong e = s[4], f = s[5], g = s[6], h = s[7];
            for (int i = 0; i < 16; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt64BigEndian(block.Slice(i * 8));
                ulong t1 = h + S1(e) + ((e & f) ^ (~e & g)) +
                           BinaryPrimitives.ReadUInt64BigEndian(K.AsSpan(i * 8)) + w[i];
                ulong t2 = S0(a) + ((a & b) ^ (a & c) ^ (b & c));
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            for (int i = 16; i < 80; i++)
            {
                int j = i & 15;
                w[j] += G0(w[(j + 1) & 15]) + w[(j + 9) & 15] + G1(w[(j + 14) & 15]);
                ulong t1 = h + S1(e) + ((e & f) ^ (~e & g)) +
                           BinaryPrimitives.ReadUInt64BigEndian(K.AsSpan(i * 8)) + w[j];
                ulong t2 = S0(a) + ((a & b) ^ (a & c) ^ (b & c));
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            s[0] += a; s[1] += b; s[2] += c; s[3] += d;
            s[4] += e; s[5] += f; s[6] += g; s[7] += h;
        }
    }
}
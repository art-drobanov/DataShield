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

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

public static class Sha256Compact // 28de873e06ca5d8a08f8e3015888dc7570224b03f8f4b029aaecb3ea18c0eefd
{
    private static readonly byte[] K = Convert.FromHexString(
    "428a2f9871374491b5c0fbcfe9b5dba53956c25b59f111f1923f82a4ab1c5ed5" +
    "d807aa9812835b01243185be550c7dc372be5d7480deb1fe9bdc06a7c19bf174" +
    "e49b69c1efbe47860fc19dc6240ca1cc2de92c6f4a7484aa5cb0a9dc76f988da" +
    "983e5152a831c66db00327c8bf597fc7c6e00bf3d5a7914706ca635114292967" +
    "27b70a852e1b21384d2c6dfc53380d13650a7354766a0abb81c2c92e92722c85" +
    "a2bfe8a1a81a664bc24b8b70c76c51a3d192e819d6990624f40e3585106aa070" +
    "19a4c1161e376c082748774c34b0bcb5391c0cb34ed8aa4a5b9cca4f682e6ff3" +
    "748f82ee78a5636f84c878148cc7020890befffaa4506cebbef9a3f7c67178f2");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint R(uint x, int n) => BitOperations.RotateRight(x, n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint S0(uint x) => R(x, 02) ^ R(x, 13) ^ R(x, 22);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint S1(uint x) => R(x, 06) ^ R(x, 11) ^ R(x, 25);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint G0(uint x) => R(x, 07) ^ R(x, 18) ^ x >> 03;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint G1(uint x) => R(x, 17) ^ R(x, 19) ^ x >> 10;

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
        int length = data.Length;
        Span<uint> s = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
                        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];
        while (data.Length >= 64) { Compress(data[..64], s); data = data[64..]; }
        Span<byte> tail = stackalloc byte[128];
        tail.Clear(); data.CopyTo(tail); tail[data.Length] = 0x80;
        int padded = data.Length < 56 ? 64 : 128;
        BinaryPrimitives.WriteUInt64BigEndian(tail.Slice(padded - 8), (ulong)length << 3);
        Compress(tail[..64], s); if (padded == 128) Compress(tail[64..], s);
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(hash.Slice(i * 4), s[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Compress(ReadOnlySpan<byte> block, Span<uint> s)
    {
        unchecked
        {
            Span<uint> w = stackalloc uint[16];
            uint a = s[0], b = s[1], c = s[2], d = s[3];
            uint e = s[4], f = s[5], g = s[6], h = s[7];
            for (int i = 0; i < 16; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4));
                uint t1 = h + S1(e) + ((e & f) ^ (~e & g)) +
                          BinaryPrimitives.ReadUInt32BigEndian(K.AsSpan(i * 4)) + w[i];
                uint t2 = S0(a) + ((a & b) ^ (a & c) ^ (b & c));
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            for (int i = 16; i < 64; i++)
            {
                int j = i & 15;
                w[j] += G0(w[(j + 1) & 15]) + w[(j + 9) & 15] + G1(w[(j + 14) & 15]);
                uint t1 = h + S1(e) + ((e & f) ^ (~e & g)) +
                          BinaryPrimitives.ReadUInt32BigEndian(K.AsSpan(i * 4)) + w[j];
                uint t2 = S0(a) + ((a & b) ^ (a & c) ^ (b & c));
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            s[0] += a; s[1] += b; s[2] += c; s[3] += d;
            s[4] += e; s[5] += f; s[6] += g; s[7] += h;
        }
    }
}
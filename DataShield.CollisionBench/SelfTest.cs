using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Selftest: корректность стенда перед измерениями
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Проверки того, что стенд сравнивает именно то, что задумано: реплика схемы A
/// при t=9 побитово эквивалентна продовому <see cref="PacketHasher"/>, схема B
/// находит ровно свой индекс, кеш сидов согласован, сценарии ловят события.
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// Запустить все группы проверок: эквивалентность схемы A продовому
    /// PacketHasher, поведение схемы B и кеша сидов, а также мини-кампании
    /// сценарной механики (движок кампании ловит события с правильной
    /// частотой). Любая неудачная проверка — исключение.
    /// </summary>
    public static void Run(long seed, int threads)
    {
        var rng = new Random(unchecked((int)seed));

        Group("Схема A: эквивалентность PacketHasher при t=9", require =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var index = rng.Next(PacketFormat.MaxDataVolumes);
                var payload = RandomBytes(rng);
                var headerHash = Scenarios.MakeHeaderHash(rng);

                var sector = ClassicScheme.BuildSector(
                    index, payload, headerHash, ClassicScheme.HashSize);

                require(sector.Length == PacketFormat.PacketSize, "размер сектора A = 75 байт");
                require(PacketHasher.VerifySectorPacket(sector, headerHash),
                    "прод-проверка принимает сектор A");
                require(
                    ClassicScheme.Verify(
                        sector, headerHash, ClassicScheme.HashSize, out var verified) &&
                    verified == index,
                    "реплика принимает сектор A и читает индекс");

                var reference = new byte[PacketFormat.PacketSize];
                reference[0] = (byte)index;
                reference[1] = (byte)(index >> 8);
                payload.CopyTo(reference, PacketFormat.SectorNumberSize);
                PacketHasher.ComputeSectorHash(
                    reference.AsSpan(0, PacketFormat.SectorContentSize), headerHash)
                    .CopyTo(reference, PacketFormat.SectorHashOffset);
                require(sector.AsSpan().SequenceEqual(reference),
                    "сборка A побитово совпадает с продовой");

                var broken = (byte[])sector.Clone();
                broken[rng.Next(PacketFormat.PacketSize)] ^= (byte)(1 + rng.Next(255));
                var replicaAccepted = ClassicScheme.Verify(
                    broken, headerHash, ClassicScheme.HashSize, out _);
                require(
                    replicaAccepted == PacketHasher.VerifySectorPacket(broken, headerHash),
                    "реплика и прод дают одинаковый вердикт на битом пакете");
                require(!replicaAccepted, "битый пакет A отвергается");

                var foreignHash = Scenarios.MakeHeaderHash(rng);
                require(!PacketHasher.VerifySectorPacket(sector, foreignHash),
                    "чужой H5 отвергается (прод)");
                require(
                    !ClassicScheme.Verify(
                        sector, foreignHash, ClassicScheme.HashSize, out _),
                    "чужой H5 отвергается (реплика)");
            }
        });

        Group("Схема B: сборка и прием с кешем сидов", require =>
        {
            foreach (var sectors in new[] { 1, 2, 7, 300, 4096 })
            {
                var headerHash = Scenarios.MakeHeaderHash(rng);
                var cache = new SectorSeedCache(headerHash, sectors);
                var foreignCache = new SectorSeedCache(
                    Scenarios.MakeHeaderHash(rng), sectors);

                require(cache.SectorCount == sectors, "SectorCount равен K");

                for (var i = 0; i < 200; i++)
                {
                    var index = rng.Next(sectors);
                    var payload = RandomBytes(rng);

                    var sector = ExperimentalScheme.BuildSector(
                        index, payload, headerHash, ExperimentalScheme.HashSize);

                    require(sector.Length == PacketFormat.PacketSize,
                        "размер сектора B = 75 байт");

                    require(
                        ExperimentalScheme.VerifyAll(
                            sector, cache, ExperimentalScheme.HashSize,
                            out var first, out var matches) &&
                        first == index && matches == 1,
                        "полный перебор находит ровно истинный индекс");

                    require(
                        ExperimentalScheme.TryVerify(
                            sector, cache, ExperimentalScheme.HashSize, out var found) &&
                        found == index,
                        "перебор с ранним выходом находит истинный индекс");

                    require(
                        !ExperimentalScheme.VerifyAll(
                            sector, foreignCache, ExperimentalScheme.HashSize,
                            out _, out var foreignMatches) && foreignMatches == 0,
                        "чужой H5 отвергается на всех K индексах");

                    var brokenPayload = (byte[])sector.Clone();
                    brokenPayload[rng.Next(PacketFormat.PayloadSize)] ^=
                        (byte)(1 + rng.Next(255));
                    require(
                        !ExperimentalScheme.VerifyAll(
                            brokenPayload, cache, ExperimentalScheme.HashSize,
                            out _, out var payloadMatches) && payloadMatches == 0,
                        "порча payload отвергается");

                    var brokenHash = (byte[])sector.Clone();
                    brokenHash[ExperimentalScheme.HashOffset +
                        rng.Next(ExperimentalScheme.HashSize)] ^=
                        (byte)(1 + rng.Next(255));
                    require(
                        !ExperimentalScheme.VerifyAll(
                            brokenHash, cache, ExperimentalScheme.HashSize,
                            out _, out var hashMatches) && hashMatches == 0,
                        "порча хеша отвергается");
                }
            }

            Span<byte> hashA = stackalloc byte[32];
            Span<byte> hashB = stackalloc byte[32];

            for (var i = 0; i < 200; i++)
            {
                var index = rng.Next(PacketFormat.MaxDataVolumes);
                var payload = RandomBytes(rng);
                var headerHash = Scenarios.MakeHeaderHash(rng);

                ClassicScheme.HashInto(index, payload, headerHash, hashA);
                ExperimentalScheme.HashInto(index, payload, headerHash, hashB);
                require(hashA.SequenceEqual(hashB),
                    "хеш-входы схем A и B совпадают (индекс виртуальный, конвенции те же)");
            }
        });

        Group("Кеш сидов", require =>
        {
            var headerHash = Scenarios.MakeHeaderHash(rng);

            for (var i = 0; i < 200; i++)
            {
                var sectors = 1 + rng.Next(PacketFormat.MaxDataVolumes);
                var cache = new SectorSeedCache(headerHash, sectors);
                var index = rng.Next(sectors);
                var prefix = cache.Prefix(index);

                require(
                    prefix.Length == SectorSeedCache.PrefixSize &&
                    prefix[..PacketFormat.HeaderHashSize].SequenceEqual(headerHash) &&
                    prefix[PacketFormat.HeaderHashSize] == (byte)(index & 0xFF) &&
                    prefix[PacketFormat.HeaderHashSize + 1] == (byte)((index >> 8) & 0xFF),
                    "префикс кеша = H5 ‖ idxLE");
            }

            require(Throws<ArgumentOutOfRangeException>(
                    () => new SectorSeedCache(headerHash, 0)),
                "K=0 отвергается");
            require(Throws<ArgumentOutOfRangeException>(
                    () => new SectorSeedCache(headerHash, PacketFormat.MaxDataVolumes + 1)),
                "K>65535 отвергается");
            require(Throws<ArgumentException>(
                    () => new SectorSeedCache(new byte[PacketFormat.HeaderHashSize - 1], 10)),
                "короткий H5 отвергается");
        });

        Group("Сценарная механика (движок ловит события)", require =>
        {
            var master = new Random(unchecked((int)seed ^ 0x5A5A));
            var headerHash = Scenarios.MakeHeaderHash(master);
            var foreignHash = Scenarios.MakeHeaderHash(master);

            // A, t=1: P = 2^-8; ячейки набирают события за доли секунды.
            var expectedA = CollisionMath.FalseAcceptClassic(1);

            var noise = Campaign.Run(
                new CellSetup("A", Scenarios.Noise, 1, 0, expectedA),
                50, 10, long.MaxValue, threads, seed ^ 101,
                Scenarios.ClassicNoise(1, headerHash));
            require(RateWithin(noise, 4), "A/шум: частота в пределах модели (×4)");

            var foreign = Campaign.Run(
                new CellSetup("A", Scenarios.ForeignFile, 1, 0, expectedA),
                50, 10, long.MaxValue, threads, seed ^ 102,
                Scenarios.ClassicForeign(1, headerHash, foreignHash));
            require(RateWithin(foreign, 4), "A/чужой файл: частота в пределах модели (×4)");

            var corrupt = Campaign.Run(
                new CellSetup("A", Scenarios.Corrupt, 1, 0, expectedA),
                50, 10, long.MaxValue, threads, seed ^ 103,
                Scenarios.ClassicCorrupt(1, headerHash));
            require(RateWithin(corrupt, 4), "A/порча: частота в пределах модели (×4)");

            // B, hb=3, K=4096: P = 2^-12; одно событие ≈ 2^24 хешей.
            var cache = new SectorSeedCache(headerHash, 4096);
            var expectedB = CollisionMath.FalseAcceptExperimental(3, 4096);

            var expNoise = Campaign.Run(
                new CellSetup("B", Scenarios.Noise, 3, 4096, expectedB),
                12, 25, long.MaxValue, threads, seed ^ 104,
                Scenarios.ExperimentalNoise(3, cache));
            require(RateWithin(expNoise, 6), "B/шум: частота в пределах модели (×6)");

            var expCorrupt = Campaign.Run(
                new CellSetup("B", Scenarios.Corrupt, 3, 4096, expectedB),
                12, 25, long.MaxValue, threads, seed ^ 105,
                Scenarios.ExperimentalCorrupt(3, cache, headerHash, 4096));
            require(RateWithin(expCorrupt, 6), "B/порча: частота в пределах модели (×6)");
        });
    }

    /// <summary>Наблюдаемая частота в пределах factor от модели и события есть.</summary>
    private static bool RateWithin(CellResult result, double factor) =>
        result.Accepts > 0 &&
        result.Setup.Expected > 0 &&
        result.ObservedRate >= result.Setup.Expected / factor &&
        result.ObservedRate <= result.Setup.Expected * factor;

    /// <summary>
    /// Группа проверок: body получает функцию require(condition, what);
    /// по завершении печатает «PASS … N проверок».
    /// </summary>
    private static void Group(string title, Action<Action<bool, string>> body)
    {
        var checks = 0;

        void Require(bool condition, string what)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException(
                    $"SELFTEST FAIL [{title}]: {what} (проверка #{checks})");
        }

        body(Require);
        Program.PrintLine($"  PASS  {title} — {checks} проверок", ConsoleColor.Green);
    }

    /// <summary>Случайный payload полного размера.</summary>
    private static byte[] RandomBytes(Random rng)
    {
        var payload = new byte[PacketFormat.PayloadSize];
        rng.NextBytes(payload);
        return payload;
    }

    /// <summary>action бросает исключение указанного типа?</summary>
    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}

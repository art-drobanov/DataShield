using System.Diagnostics;
using DataShield.Codec.Ecc;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Subsets;
using DataShield.Codec.StreamProcessor.Versions;
using Xunit;

namespace DataShield.Codec.StreamProcessor.Tests;

public sealed class ReceptionSlotTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  Накопление версий
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(100)]
    public void AddSector_ExactCopies_AreAccumulatedInSingleVariant(int receptionCount)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 100);
        var slot = CreateSlot(file);
        var payload = BuildDataSectors(file)[0];

        for (var i = 0; i < receptionCount; i++)
            Assert.True(slot.AddSector(0, payload.ToArray()));

        var versions = slot.GetSectorVersions(0);

        var version = Assert.Single(versions);
        Assert.Equal(receptionCount, version.ConfirmationCount);
        AssertPayloadEqual(payload, version.Payload);

        Assert.Equal(1, slot.ReceivedSectorCount);
        Assert.Equal(receptionCount, slot.ReceivedSectorCopyCount);
        Assert.Equal(0, slot.CollisionSectorCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void AddSector_DifferentPayloads_CreateDifferentVariants(int variantCount)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 101);
        var slot = CreateSlot(file);
        var expectedPayloads = new List<byte[]>();

        for (var v = 0; v < variantCount; v++)
        {
            var payload = MakeVariant(v + 1);
            expectedPayloads.Add(payload);
            Assert.True(slot.AddSector(0, payload));
        }

        var versions = slot.GetSectorVersions(0);

        Assert.Equal(variantCount, versions.Count);
        Assert.All(versions, v => Assert.Equal(1, v.ConfirmationCount));

        // Все варианты равновероятны, поэтому сохраняется порядок поступления.
        for (var i = 0; i < variantCount; i++)
            AssertPayloadEqual(expectedPayloads[i], versions[i].Payload);

        Assert.Equal(1, slot.ReceivedSectorCount);
        Assert.Equal(variantCount, slot.ReceivedSectorCopyCount);
        Assert.Equal(1, slot.CollisionSectorCount);
    }

    [Theory]
    [InlineData(3, 2, 1)]
    [InlineData(1, 2, 3)]
    [InlineData(2, 5, 3)]
    [InlineData(4, 4, 1)]
    [InlineData(3, 3, 3)]
    [InlineData(10, 1, 10)]
    public void AddSector_VariantsAreSortedByConfirmationCountDescending(
        int firstCount, int secondCount, int thirdCount)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 102);
        var slot = CreateSlot(file);

        var payloads = new[] { MakeVariant(11), MakeVariant(22), MakeVariant(33) };
        var counts = new[] { firstCount, secondCount, thirdCount };

        // Каждый payload добавляется отдельной группой. OrderByDescending —
        // стабильная сортировка: при равных счётчиках ожидаемый порядок
        // совпадает с первоначальным порядком поступления.
        for (var p = 0; p < payloads.Length; p++)
            for (var copy = 0; copy < counts[p]; copy++)
                Assert.True(slot.AddSector(0, payloads[p].ToArray()));

        var expectedOrder = counts
            .Select((count, index) => new { Count = count, Payload = payloads[index] })
            .OrderByDescending(x => x.Count)
            .ToArray();

        var actual = slot.GetSectorVersions(0);

        Assert.Equal(expectedOrder.Length, actual.Count);

        for (var i = 0; i < expectedOrder.Length; i++)
        {
            Assert.Equal(expectedOrder[i].Count, actual[i].ConfirmationCount);
            AssertPayloadEqual(expectedOrder[i].Payload, actual[i].Payload);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    public void AddSector_WhenVariantBecomesMostConfirmed_ItMovesToFirstPlace(
        int finalConfirmationCount)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 103);
        var slot = CreateSlot(file);

        var firstPayload = MakeVariant(1);
        var promotedPayload = MakeVariant(2);

        Assert.True(slot.AddSector(0, firstPayload));
        Assert.True(slot.AddSector(0, promotedPayload));

        for (var i = 1; i < finalConfirmationCount; i++)
            Assert.True(slot.AddSector(0, promotedPayload.ToArray()));

        var versions = slot.GetSectorVersions(0);

        Assert.Equal(2, versions.Count);
        Assert.Equal(finalConfirmationCount, versions[0].ConfirmationCount);
        AssertPayloadEqual(promotedPayload, versions[0].Payload);

        Assert.Equal(1, versions[1].ConfirmationCount);
        AssertPayloadEqual(firstPayload, versions[1].Payload);
    }

    [Theory]
    [InlineData(-1, 64)]
    [InlineData(2, 64)]
    [InlineData(3, 64)]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 63)]
    [InlineData(0, 65)]
    [InlineData(0, 128)]
    public void AddSector_InvalidSectorOrPayloadLength_IsRejected(
        int sectorNumber, int payloadLength)
    {
        // Ровно два data-сектора: допустимые номера — 0 и 1.
        var file = MakeFile(PacketFormat.PayloadSize * 2, seed: 104);
        var slot = CreateSlot(file);

        var accepted = slot.AddSector(sectorNumber, new byte[payloadLength]);

        Assert.False(accepted);
        Assert.Equal(0, slot.ReceivedSectorCount);
        Assert.Equal(0, slot.ReceivedSectorCopyCount);
        Assert.Equal(0, slot.CollisionSectorCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Карта валидности и статистика
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildValidityMap_MultipleVersionsOccupyOneLogicalSector()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 4, seed: 105);
        var slot = CreateSlot(file);

        var sector0A = MakeVariant(1);
        var sector0B = MakeVariant(2);
        var sector2 = MakeVariant(3);

        Assert.True(slot.AddSector(0, sector0A));
        Assert.True(slot.AddSector(0, sector0A.ToArray()));
        Assert.True(slot.AddSector(0, sector0B));
        Assert.True(slot.AddSector(2, sector2));

        Assert.Equal(new[] { true, false, true, false }, slot.BuildValidityMap());
        // Сектор 0 — коллизия версий ('▓'), сектор 2 — единственная версия ('█')
        Assert.Equal("▓░█░", slot.FormatValidityMap());

        Assert.Equal(2, slot.ReceivedSectorCount);
        Assert.Equal(4, slot.ReceivedSectorCopyCount);
        Assert.Equal(1, slot.CollisionSectorCount);
        Assert.Equal(50.0, slot.Coverage, precision: 10);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Обычная прямая сборка
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(511)]
    public void TryAssemble_TrimsPaddingAndVerifiesSha256(int fileLength)
    {
        var file = MakeFile(fileLength, seed: 200 + fileLength);
        var slot = CreateSlot(file);
        var sectors = BuildDataSectors(file);

        for (var i = 0; i < sectors.Length; i++)
            Assert.True(slot.AddSector(i, sectors[i]));

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(10, 9)]
    public void TryAssemble_MissingDataSector_ReturnsNull(
        int sectorCount, int missingSector)
    {
        var file = MakeFile(sectorCount * PacketFormat.PayloadSize, seed: 300 + sectorCount);
        var slot = CreateSlot(file);
        var sectors = BuildDataSectors(file);

        for (var i = 0; i < sectors.Length; i++)
        {
            if (i == missingSector) continue;
            Assert.True(slot.AddSector(i, sectors[i]));
        }

        Assert.Null(slot.TryAssemble());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    public void TryAssemble_UsesMostConfirmedVariantFirst(int correctConfirmationCount)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 106);
        var correct = BuildDataSectors(file)[0];
        var wrong = CreateWrongPayload(correct, salt: 1);

        var slot = CreateSlot(file);

        // Неверный сектор пришёл первым.
        Assert.True(slot.AddSector(0, wrong));

        // Верная версия получила больше подтверждений.
        for (var i = 0; i < correctConfirmationCount; i++)
            Assert.True(slot.AddSector(0, correct.ToArray()));

        var versions = slot.GetSectorVersions(0);

        AssertPayloadEqual(correct, versions[0].Payload);
        Assert.Equal(correctConfirmationCount, versions[0].ConfirmationCount);

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Полный перебор равновероятных вариантов
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2)] // 2 комбинации
    [InlineData(2, 2)] // 4 комбинации
    [InlineData(3, 2)] // 8 комбинаций
    [InlineData(5, 2)] // 32 комбинации
    [InlineData(2, 3)] // 9 комбинаций
    [InlineData(3, 3)] // 27 комбинаций
    [InlineData(4, 3)] // 81 комбинация
    public void TryAssemble_ExhaustiveSearch_FindsCorrectCombination(
        int ambiguousSectorCount, int variantsPerSector)
    {
        var data = MakeData(ambiguousSectorCount, PacketFormat.PayloadSize,
            new Random(400 + ambiguousSectorCount * 10 + variantsPerSector));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1_000_000,
            MaxHeuristicAttempts = 1_000,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        // Верный вариант добавляется последним. Все варианты имеют по одному
        // подтверждению, поэтому верная комбинация — в самом конце перебора.
        for (var sector = 0; sector < ambiguousSectorCount; sector++)
            AddVariantsWithCorrectAt(slot, sector, data[sector],
                variantsPerSector, correctVariantIndex: variantsPerSector - 1);

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void TryAssemble_ExhaustiveSearch_DoesNotRequireEverySectorToBeAmbiguous(
        int unambiguousSectorCount)
    {
        const int AmbiguousSectorCount = 2;
        var total = AmbiguousSectorCount + unambiguousSectorCount;

        var data = MakeData(total, PacketFormat.PayloadSize,
            new Random(500 + unambiguousSectorCount));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        // В двух первых слотах верная версия стоит второй.
        for (var sector = 0; sector < AmbiguousSectorCount; sector++)
            AddVariantsWithCorrectAt(slot, sector, data[sector],
                variantCount: 2, correctVariantIndex: 1);

        // Остальные слоты имеют только одну версию.
        for (var sector = AmbiguousSectorCount; sector < total; sector++)
            Assert.True(slot.AddSector(sector, data[sector]));

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_LessConfirmedCorrectVariant_IsNotIncludedInSearch()
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 107);
        var correct = BuildDataSectors(file)[0];
        var wrongA = CreateWrongPayload(correct, salt: 1);
        var wrongB = CreateWrongPayload(correct, salt: 2);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        // Два неверных варианта получают по два подтверждения.
        Assert.True(slot.AddSector(0, wrongA));
        Assert.True(slot.AddSector(0, wrongA.ToArray()));
        Assert.True(slot.AddSector(0, wrongB));
        Assert.True(slot.AddSector(0, wrongB.ToArray()));

        // Верный вариант имеет только одно подтверждение.
        Assert.True(slot.AddSector(0, correct));

        var versions = slot.GetSectorVersions(0);

        Assert.Equal(3, versions.Count);
        Assert.Equal(2, versions[0].ConfirmationCount);
        Assert.Equal(2, versions[1].ConfirmationCount);
        Assert.Equal(1, versions[2].ConfirmationCount);
        AssertPayloadEqual(correct, versions[2].Payload);

        // В переборе участвуют только первые два варианта с максимальным
        // счётчиком; менее подтверждённая верная версия не проверяется.
        Assert.Null(slot.TryAssemble());
    }

    [Fact]
    public void TryAssemble_NonLeadingVariantsWithSameLowerCount_AreNotSearched()
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 108);
        var correct = BuildDataSectors(file)[0];
        var topWrong = CreateWrongPayload(correct, salt: 1);
        var lowerWrong = CreateWrongPayload(correct, salt: 2);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        // Единственный наиболее подтверждённый вариант.
        for (var i = 0; i < 3; i++)
            Assert.True(slot.AddSector(0, topWrong.ToArray()));

        // Две версии с одинаковым, но меньшим счётчиком.
        for (var i = 0; i < 2; i++)
            Assert.True(slot.AddSector(0, lowerWrong.ToArray()));

        for (var i = 0; i < 2; i++)
            Assert.True(slot.AddSector(0, correct.ToArray()));

        var versions = slot.GetSectorVersions(0);

        Assert.Equal(new[] { 3, 2, 2 },
            versions.Select(x => x.ConfirmationCount).ToArray());

        // Равенство lowerWrong и correct между собой не имеет значения:
        // они не равновероятны с первым, наиболее подтверждённым вариантом.
        Assert.Null(slot.TryAssemble());
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Эвристическая прокрутка
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(3, 3, 2)]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 4)]
    public void TryAssemble_RotationHeuristic_FindsSynchronizedCombination(
        int sectorCount, int variantsPerSector, int correctVariantIndex)
    {
        var data = MakeData(sectorCount, PacketFormat.PayloadSize,
            new Random(600 + sectorCount * 10 + variantsPerSector));
        var file = Flatten(data);

        // Любое число комбинаций > 1 принудительно переводит поиск
        // в эвристический режим.
        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1,
            MaxHeuristicAttempts = variantsPerSector,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        for (var sector = 0; sector < sectorCount; sector++)
            AddVariantsWithCorrectAt(slot, sector, data[sector],
                variantsPerSector, correctVariantIndex);

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);

        // Эвристика изменяет порядок List: после успешного числа прокруток
        // верные варианты должны оказаться первыми.
        for (var sector = 0; sector < sectorCount; sector++)
            AssertPayloadEqual(data[sector], slot.GetSectorVersions(sector)[0].Payload);
    }

    [Theory]
    [InlineData(1, 1)] // состояние t = 1
    [InlineData(0, 1)] // состояние t = 4: 4 mod 2 = 0, 4 mod 3 = 1
    [InlineData(1, 2)] // состояние t = 5: 5 mod 2 = 1, 5 mod 3 = 2
    public void TryAssemble_RotationHeuristic_HandlesListsOfDifferentLengths(
        int correctIndexForTwoVariants, int correctIndexForThreeVariants)
    {
        var data = MakeData(2, PacketFormat.PayloadSize, new Random(700));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1,
            MaxHeuristicAttempts = 6,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        AddVariantsWithCorrectAt(slot, sectorNumber: 0, correctPayload: data[0],
            variantCount: 2, correctVariantIndex: correctIndexForTwoVariants);
        AddVariantsWithCorrectAt(slot, sectorNumber: 1, correctPayload: data[1],
            variantCount: 3, correctVariantIndex: correctIndexForThreeVariants);

        // Состояния синхронной прокрутки повторяются через НОК(2, 3) = 6.
        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);

        AssertPayloadEqual(data[0], slot.GetSectorVersions(0)[0].Payload);
        AssertPayloadEqual(data[1], slot.GetSectorVersions(1)[0].Payload);
    }

    [Theory]
    [InlineData(5, 4, 4)]
    [InlineData(6, 5, 5)]
    [InlineData(10, 3, 9)]
    public void TryAssemble_RotationHeuristic_StopsAtAttemptLimit(
        int variantCount, int maxAttempts, int correctVariantIndex)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 701);
        var correct = BuildDataSectors(file)[0];

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1,
            MaxHeuristicAttempts = maxAttempts,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        AddVariantsWithCorrectAt(slot, sectorNumber: 0, correctPayload: correct,
            variantCount, correctVariantIndex);

        // Исходное состояние считается первой попыткой. При maxAttempts = 4
        // будут проверены индексы 0..3, но не индекс 4.
        Assert.Null(slot.TryAssemble());
    }

    [Fact]
    public void TryAssemble_AfterFailedHeuristicSearch_KeepsRotatedOrder()
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 702);
        var correct = BuildDataSectors(file)[0];

        var wrong0 = CreateWrongPayload(correct, salt: 1);
        var wrong1 = CreateWrongPayload(correct, salt: 2);
        var wrong2 = CreateWrongPayload(correct, salt: 3);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1,
            MaxHeuristicAttempts = 2,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        Assert.True(slot.AddSector(0, wrong0));
        Assert.True(slot.AddSector(0, wrong1));
        Assert.True(slot.AddSector(0, wrong2));

        Assert.Null(slot.TryAssemble());

        // Проверялись состояния:
        //   0: wrong0, wrong1, wrong2
        //   1: wrong1, wrong2, wrong0
        // После одной прокрутки первым остаётся wrong1.
        var versions = slot.GetSectorVersions(0);

        AssertPayloadEqual(wrong1, versions[0].Payload);
        AssertPayloadEqual(wrong2, versions[1].Payload);
        AssertPayloadEqual(wrong0, versions[2].Payload);
    }

    [Fact]
    public void ConfirmationAfterRotation_RestoresCountBasedOrdering()
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 703);
        var correct = BuildDataSectors(file)[0];

        var wrong0 = CreateWrongPayload(correct, salt: 1);
        var wrong1 = CreateWrongPayload(correct, salt: 2);
        var wrong2 = CreateWrongPayload(correct, salt: 3);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1,
            MaxHeuristicAttempts = 2,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        Assert.True(slot.AddSector(0, wrong0));
        Assert.True(slot.AddSector(0, wrong1));
        Assert.True(slot.AddSector(0, wrong2));

        // После неудачного поиска порядок станет wrong1, wrong2, wrong0.
        Assert.Null(slot.TryAssemble());

        // wrong0 получает дополнительное подтверждение и обязан подняться.
        Assert.True(slot.AddSector(0, wrong0.ToArray()));

        var versions = slot.GetSectorVersions(0);

        Assert.Equal(2, versions[0].ConfirmationCount);
        AssertPayloadEqual(wrong0, versions[0].Payload);

        Assert.Equal(1, versions[1].ConfirmationCount);
        Assert.Equal(1, versions[2].ConfirmationCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Бюджет времени, неполнота эвристики, коллизии ECC, прогресс, снимки
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAssemble_TimeBudgetExhausted_ReturnsNullPromptly()
    {
        // Два спорных сектора по ~1000 вариантов: C ≈ 10⁶ — перебор запрещён,
        // число состояний прокрутки зажато лимитом. Бюджет 1 мс проверяется
        // между попытками: сборка обязана отказаться быстро, а не зависнуть.
        var data = MakeData(2, PacketFormat.PayloadSize, new Random(710));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100_000,
            MaxHeuristicAttempts = 100_000,
            TimeBudget = TimeSpan.FromMilliseconds(1)
        });

        const int VariantCount = 997;

        for (var v = 0; v < VariantCount; v++)
        {
            // Правильных версий нет нигде — сборка невозможна в принципе;
            // измеряется только скорость честного отказа.
            Assert.True(slot.AddSector(0, CreateWrongPayload(data[0], salt: 10_000 + v)));
            Assert.True(slot.AddSector(1, CreateWrongPayload(data[1], salt: 20_000 + v)));
        }

        var timer = Stopwatch.StartNew();
        Assert.Null(slot.TryAssemble());
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Поиск с бюджетом 1 мс работал {timer.Elapsed.TotalMilliseconds:F0} мс.");
    }

    [Fact]
    public void TryAssemble_RotationWithCommonDivisors_CannotReachMixedCombination()
    {
        // T = (2, 2, 2): C = 8, но НОК = 2 — синхронная прокрутка проверяет
        // только диагонали (0,0,0) и (1,1,1). Смешанная комбинация (0,1,0)
        // для эвристики недостижима: документированная плата за скорость.
        var data = MakeData(3, PacketFormat.PayloadSize, new Random(711));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 1, // форсируем эвристическую прокрутку
            MaxHeuristicAttempts = 100_000,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        AddVariantsWithCorrectAt(slot, 0, data[0], variantCount: 2, correctVariantIndex: 0);
        AddVariantsWithCorrectAt(slot, 1, data[1], variantCount: 2, correctVariantIndex: 1);
        AddVariantsWithCorrectAt(slot, 2, data[2], variantCount: 2, correctVariantIndex: 0);

        Assert.Null(slot.TryAssemble());
    }

    [Fact]
    public void TryAssemble_ExhaustiveFindsCombinationMissedByRotation()
    {
        // Та же конфигурация, что и в тесте о неполноте прокрутки, но с
        // полным перебором: одометр посещает все 8 комбинаций и находит (0,1,0).
        var data = MakeData(3, PacketFormat.PayloadSize, new Random(712));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100_000,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        AddVariantsWithCorrectAt(slot, 0, data[0], variantCount: 2, correctVariantIndex: 0);
        AddVariantsWithCorrectAt(slot, 1, data[1], variantCount: 2, correctVariantIndex: 1);
        AddVariantsWithCorrectAt(slot, 2, data[2], variantCount: 2, correctVariantIndex: 0);

        var result = slot.TryAssemble();

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_EccSectorCollision_SearchSelectsCorrectEccVolume()
    {
        // N = 2, M = 1: data-том 1 не принят вовсе, восстановление целиком
        // зависит от ECC-тома №2. У ECC-номера — коллизия версий (подделка
        // пришла первой). Прямая сборка невозможна, и перебор обязан
        // варьировать именно ECC-версию: подделка портит RS-восстановление.
        var file = MakeFile(PacketFormat.PayloadSize * 2, seed: 713);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 1);

        var slot = CreateSlot(file, eccCount: 1, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100_000,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        Assert.True(slot.AddSector(0, dataSectors[0])); // данные: только том 0
        AddVariantsWithCorrectAt(slot, 2, ecc[0],       // ECC: подделка + истина
            variantCount: 2, correctVariantIndex: 1);

        var result = slot.TryAssemble(new RsCodecAdapter());

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_WrongEccOnly_CannotRecoverMissingData()
    {
        // Контроль к предыдущему тесту: единственная (поддельная) версия ECC
        // без коллизии — точек ветвления нет, RS восстанавливает неверные
        // данные, финальный SHA-256 не сходится.
        var file = MakeFile(PacketFormat.PayloadSize * 2, seed: 714);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 1);
        var wrongEcc = CreateWrongPayload(ecc[0], salt: 99);

        var slot = CreateSlot(file, eccCount: 1);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(2, wrongEcc));

        Assert.Null(slot.TryAssemble(new RsCodecAdapter()));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Стадия 3: подбор подмножества томов
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAssemble_SilentlyCorruptDataVolume_SubsetSearchRecoversFile()
    {
        // Повреждение, невидимое для прежних механизмов: payload data-тома 2
        // испорчен, но принят одной версией (имитация сошедшегося усечённого
        // хеша D3) — точки ветвления нет, RS по полной карте принимает его
        // за истину. Подбор подмножества исключает повреждённый том и
        // восстанавливает его по ECC.
        var file = MakeFile(PacketFormat.PayloadSize * 4, seed: 750);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 2);

        var slot = CreateSlot(file, eccCount: 2);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(1, dataSectors[1]));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 21)));
        Assert.True(slot.AddSector(3, dataSectors[3]));
        Assert.True(slot.AddSector(4, ecc[0]));
        Assert.True(slot.AddSector(5, ecc[1]));

        var result = slot.TryAssemble(new RsCodecAdapter());

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_SilentlyCorruptEccVolume_SubsetSearchRecoversFile()
    {
        // Дыры в data (тома 1 и 2 не приняты) плюс испорченный ECC-том 0:
        // RS по полной карте строит уравнения с испорченной чётностью и
        // восстанавливает мусор. Исключение повреждённого ECC переключает
        // систему на валидные уравнения.
        var file = MakeFile(PacketFormat.PayloadSize * 3, seed: 751);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 3);

        var slot = CreateSlot(file, eccCount: 3);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(3, CreateWrongPayload(ecc[0], salt: 22)));
        Assert.True(slot.AddSector(4, ecc[1]));
        Assert.True(slot.AddSector(5, ecc[2]));

        var result = slot.TryAssemble(new RsCodecAdapter());

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_CollisionAndCorruptData_SubsetSearchRecoversFile()
    {
        // Коллизия на data-томе 0 (подделка пришла первой) плюс тихо
        // испорченный data-том 2. Коллизионный слот уходит в базу исключений
        // и восстанавливается RS; перебор версий комбинируется с исключением
        // повреждённого тома.
        var file = MakeFile(PacketFormat.PayloadSize * 3, seed: 752);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 2);

        var slot = CreateSlot(file, eccCount: 2);

        AddVariantsWithCorrectAt(slot, 0, dataSectors[0],
            variantCount: 2, correctVariantIndex: 1);
        Assert.True(slot.AddSector(1, dataSectors[1]));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 23)));
        Assert.True(slot.AddSector(3, ecc[0]));
        Assert.True(slot.AddSector(4, ecc[1]));

        var result = slot.TryAssemble(new RsCodecAdapter());

        Assert.NotNull(result);
        Assert.Equal(file, result);
    }

    [Fact]
    public void TryAssemble_CollisionsBeyondEccBudget_HonestNull()
    {
        // N=2, M=1: обе data с коллизиями, истина в обеих — в меньшинстве.
        // База исключений (оба тома) не влезает в бюджет ECC — стадия 3
        // пропускается, результат — честный отказ.
        var file = MakeFile(PacketFormat.PayloadSize * 2, seed: 753);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 1);

        var slot = CreateSlot(file, eccCount: 1);

        foreach (var (index, salt) in new[] { (0, 31), (1, 32) })
        {
            var fake = CreateWrongPayload(dataSectors[index], salt: salt);

            Assert.True(slot.AddSector(index, fake));
            Assert.True(slot.AddSector(index, fake.ToArray()));
            Assert.True(slot.AddSector(index, dataSectors[index].ToArray()));
        }

        Assert.True(slot.AddSector(2, ecc[0]));

        Assert.Null(slot.TryAssemble(new RsCodecAdapter()));
    }

    [Fact]
    public void TryAssemble_SubsetSearchExhaustedWithoutSuccess_ReturnsNull()
    {
        // Два тихо испорчённых data-тома при M=1: пары исключений
        // неосуществимы (2 > 1), каждая одиночная попытка оставляет второй
        // повреждённый том в доверии. Все маски исчерпаны — честный отказ.
        var file = MakeFile(PacketFormat.PayloadSize * 3, seed: 754);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 1);

        var slot = CreateSlot(file, eccCount: 1);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(1, CreateWrongPayload(dataSectors[1], salt: 41)));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 42)));
        Assert.True(slot.AddSector(3, ecc[0]));

        Assert.Null(slot.TryAssemble(new RsCodecAdapter()));
    }

    [Fact]
    public void TryAssemble_SubsetSearchTinyBudget_ReturnsNullPromptly()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 3, seed: 755);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 1);

        var slot = CreateSlot(file, eccCount: 1, options: new SectorVersionSearchOptions
        {
            SubsetSearch = new VolumeSubsetSearchOptions
            {
                TimeBudget = TimeSpan.FromTicks(1)
            }
        });

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(1, CreateWrongPayload(dataSectors[1], salt: 51)));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 52)));
        Assert.True(slot.AddSector(3, ecc[0]));

        Assert.Null(slot.TryAssemble(new RsCodecAdapter()));
    }

    [Fact]
    public void TryAssemble_SubsetSearchProgress_StaysBelow100UntilSuccess()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 4, seed: 756);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 2);

        var slot = CreateSlot(file, eccCount: 2);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(1, dataSectors[1]));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 61)));
        Assert.True(slot.AddSector(3, dataSectors[3]));
        Assert.True(slot.AddSector(4, ecc[0]));
        Assert.True(slot.AddSector(5, ecc[1]));

        var reported = new List<CodecProgress>();

        var result = slot.TryAssemble(
            new RsCodecAdapter(), new ProgressCollector(reported), default);

        Assert.NotNull(result);
        Assert.Equal(file, result);

        Assert.NotEmpty(reported);
        Assert.All(reported.SkipLast(1), p => Assert.InRange(p.Percent, 0, 99));
        Assert.Equal(100, reported[^1].Percent);
        Assert.Equal(CodecStrings.AssemblyFinished, reported[^1].Phase);
        Assert.Contains(reported, p => p.Phase.Contains(CodecStrings.VolumeSubsetSearch));
    }

    [Fact]
    public void TryAssemble_SubsetSearchCancelledByProgress_ThrowsOperationCanceledException()
    {
        // Отмена при первом же отчёте стадии подбора: до попыток не доходит.
        var file = MakeFile(PacketFormat.PayloadSize * 4, seed: 757);
        var dataSectors = BuildDataSectors(file);
        var ecc = new RsCodecAdapter().Encode(dataSectors, eccCount: 2);

        var slot = CreateSlot(file, eccCount: 2);

        Assert.True(slot.AddSector(0, dataSectors[0]));
        Assert.True(slot.AddSector(1, dataSectors[1]));
        Assert.True(slot.AddSector(2, CreateWrongPayload(dataSectors[2], salt: 71)));
        Assert.True(slot.AddSector(3, dataSectors[3]));
        Assert.True(slot.AddSector(4, ecc[0]));
        Assert.True(slot.AddSector(5, ecc[1]));

        using var cts = new CancellationTokenSource();
        var reported = new List<CodecProgress>();
        var cancelling = new CancellingProgressCollector(reported, cts);

        Assert.Throws<OperationCanceledException>(
            () => slot.TryAssemble(new RsCodecAdapter(), cancelling, cts.Token));
    }

    [Fact]
    public void TryAssemble_SearchProgress_StaysBelow100UntilSuccess()
    {
        // C = 4, истина на индексе 2 — до успеха идут две неудачные попытки.
        // Троттлинг репортов поиска не должен показывать 100 до факта сборки.
        var data = MakeData(1, PacketFormat.PayloadSize, new Random(715));
        var file = Flatten(data);

        var slot = CreateSlot(file, options: new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 100_000,
            MaxHeuristicAttempts = 100,
            TimeBudget = TimeSpan.FromSeconds(10)
        });

        AddVariantsWithCorrectAt(slot, 0, data[0], variantCount: 4, correctVariantIndex: 2);

        var reported = new List<CodecProgress>();

        var result = slot.TryAssemble(rs: null, new ProgressCollector(reported), default);

        Assert.NotNull(result);
        Assert.Equal(file, result);

        Assert.NotEmpty(reported);
        Assert.All(reported.SkipLast(1), p => Assert.InRange(p.Percent, 0, 99));

        Assert.Equal(100, reported[^1].Percent);
        Assert.Equal(CodecStrings.AssemblyFinished, reported[^1].Phase);
    }

    [Fact]
    public void GetSectorVersions_SnapshotDoesNotTrackLaterReception()
    {
        // Снимок версий содержит копии: после новых подтверждений слот
        // меняется, а ранее снятый снимок остаётся в прежнем состоянии.
        var file = MakeFile(PacketFormat.PayloadSize, seed: 716);
        var slot = CreateSlot(file);
        var payload = BuildDataSectors(file)[0];

        Assert.True(slot.AddSector(0, payload));

        var snapshot = slot.GetSectorVersions(0);
        Assert.Equal(1, Assert.Single(snapshot).ConfirmationCount);

        Assert.True(slot.AddSector(0, payload.ToArray()));

        Assert.Equal(1, Assert.Single(snapshot).ConfirmationCount);
        Assert.Equal(2, Assert.Single(slot.GetSectorVersions(0)).ConfirmationCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Отмена и настройки
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAssemble_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 800);
        var slot = CreateSlot(file);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => slot.TryAssemble(rs: null, progress: null, ct: cts.Token));
    }

    [Theory]
    [InlineData(0, 1, 1_000)]
    [InlineData(-1, 1, 1_000)]
    [InlineData(1, 0, 1_000)]
    [InlineData(1, -1, 1_000)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, -1)]
    public void Constructor_InvalidSearchOptions_Throws(
        long maxExhaustiveCombinations, int maxHeuristicAttempts, int timeBudgetMilliseconds)
    {
        var file = MakeFile(PacketFormat.PayloadSize, seed: 801);
        var header = MakeHeader(file, eccCount: 0);
        var headerBytes = header.ToBytes();
        var headerHash = PacketHasher.ComputeHeaderHash(headerBytes);

        var options = new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = maxExhaustiveCombinations,
            MaxHeuristicAttempts = maxHeuristicAttempts,
            TimeBudget = TimeSpan.FromMilliseconds(timeBudgetMilliseconds)
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReceptionSlot(headerBytes, header, headerHash, options));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(63, 1)]
    [InlineData(64, 1)]
    [InlineData(65, 2)]
    [InlineData(127, 2)]
    [InlineData(128, 2)]
    [InlineData(129, 3)]
    [InlineData(639, 10)]
    [InlineData(640, 10)]
    [InlineData(641, 11)]
    public void MakeHeader_DataVolumeCount_IsDerivedFromFileSize(
        int fileSize, int expectedDataVolumeCount)
    {
        var file = MakeFile(fileSize, seed: 900 + fileSize);
        var header = MakeHeader(file, eccCount: 0);

        Assert.Equal((uint)fileSize, header.FileSize);
        Assert.Equal(expectedDataVolumeCount, header.DataVolumeCount);
        Assert.Equal(expectedDataVolumeCount, header.TotalVolumeCount);
        Assert.Equal((ushort)0, header.EccCount);
        Assert.Equal(Sha256Compact.HashData(file), header.Sha256);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(64, 7)]
    [InlineData(65, 3)]
    [InlineData(640, 10)]
    public void MakeHeader_TotalVolumeCount_IncludesEcc(int fileSize, int eccCount)
    {
        var file = MakeFile(fileSize, seed: 1_000 + fileSize + eccCount);
        var header = MakeHeader(file, eccCount);

        var expectedDataVolumeCount =
            (fileSize + PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize;

        Assert.Equal(expectedDataVolumeCount, header.DataVolumeCount);
        Assert.Equal((ushort)eccCount, header.EccCount);
        Assert.Equal(expectedDataVolumeCount + eccCount, header.TotalVolumeCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(64, 1)]
    [InlineData(65, 7)]
    [InlineData(1_024, 10)]
    public void MakeHeader_Roundtrip_PreservesFields(int fileSize, int eccCount)
    {
        var file = MakeFile(fileSize, seed: 1_100 + fileSize + eccCount);
        var original = MakeHeader(file, eccCount);

        var bytes = original.ToBytes();

        Assert.Equal(PacketFormat.HeaderContentSize, bytes.Length);

        var restored = HeaderContent.ReadFrom(bytes);

        Assert.Equal("test.dat", restored.FileName);
        Assert.Equal((uint)fileSize, restored.FileSize);
        Assert.Equal(Sha256Compact.HashData(file), restored.Sha256);
        Assert.Equal((ushort)eccCount, restored.EccCount);
        Assert.Equal(original.DataVolumeCount, restored.DataVolumeCount);
        Assert.Equal(original.TotalVolumeCount, restored.TotalVolumeCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Вспомогательные методы
    // ────────────────────────────────────────────────────────────────────────

    private static ReceptionSlot CreateSlot(
        byte[] file, int eccCount = 0, SectorVersionSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length == 0)
            throw new ArgumentException(
                "Для данных тестов файл не должен быть пустым.", nameof(file));

        var header = MakeHeader(file, eccCount);
        var headerBytes = header.ToBytes();

        Assert.Equal(PacketFormat.HeaderContentSize, headerBytes.Length);

        // H5 заголовка является сидом для хеша секторов. Для прямой
        // сборки значение не принципиально, но тестовый ReceptionSlot лучше
        // создавать в полностью корректном состоянии.
        var headerHash = PacketHasher.ComputeHeaderHash(headerBytes);

        return new ReceptionSlot(
            headerBytes, header, headerHash, options ?? SectorVersionSearchOptions.Default);
    }

    private static HeaderContent MakeHeader(byte[] file, int eccCount)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new HeaderContent
        {
            FileName = "test.dat",
            FileSize = checked((uint)file.Length),
            Sha256 = Sha256Compact.HashData(file),
            EccCount = checked((ushort)eccCount),
        };
    }

    private static byte[][] BuildDataSectors(byte[] file)
    {
        var sectorCount =
            (file.Length + PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize;
        var sectors = new byte[sectorCount][];

        for (var sector = 0; sector < sectorCount; sector++)
        {
            var payload = new byte[PacketFormat.PayloadSize];
            var sourceOffset = sector * PacketFormat.PayloadSize;
            var copyLength = Math.Min(
                file.Length - sourceOffset, PacketFormat.PayloadSize);

            file.AsSpan(sourceOffset, copyLength).CopyTo(payload);
            sectors[sector] = payload;
        }

        return sectors;
    }

    private static void AddVariantsWithCorrectAt(
        ReceptionSlot slot, int sectorNumber, byte[] correctPayload,
        int variantCount, int correctVariantIndex)
    {
        Assert.InRange(correctVariantIndex, 0, variantCount - 1);
        Assert.Equal(PacketFormat.PayloadSize, correctPayload.Length);

        for (var v = 0; v < variantCount; v++)
        {
            var payload = v == correctVariantIndex
                ? correctPayload.ToArray()
                : CreateWrongPayload(correctPayload,
                    salt: sectorNumber * 1_000 + v + 1);

            Assert.True(slot.AddSector(sectorNumber, payload));
        }

        var versions = slot.GetSectorVersions(sectorNumber);

        Assert.Equal(variantCount, versions.Count);
        Assert.All(versions, v => Assert.Equal(1, v.ConfirmationCount));
    }

    private static byte[] CreateWrongPayload(byte[] correctPayload, int salt)
    {
        var result = correctPayload.ToArray();

        // Гарантированно меняем хотя бы один байт. Для используемых в тестах
        // salt получаются разные payload.
        var firstPosition = Math.Abs(salt * 17) % result.Length;
        var secondPosition = (firstPosition + 23) % result.Length;
        var firstMask = (byte)(1 + Math.Abs(salt % 251));
        var secondMask = (byte)(1 + Math.Abs((salt * 7) % 251));

        result[firstPosition] ^= firstMask;
        result[secondPosition] ^= secondMask;

        Assert.False(result.AsSpan().SequenceEqual(correctPayload));

        return result;
    }

    private static byte[] MakeVariant(int variantNumber)
    {
        var payload = new byte[PacketFormat.PayloadSize];

        for (var i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(variantNumber * 31 + i * 17));

        return payload;
    }

    private static byte[] MakeFile(int length, int seed)
    {
        var result = new byte[length];
        new Random(seed).NextBytes(result);
        return result;
    }

    private static byte[] Flatten(byte[][] data)
    {
        var result = new byte[data.Sum(x => x.Length)];
        var offset = 0;

        foreach (var payload in data)
        {
            payload.CopyTo(result, offset);
            offset += payload.Length;
        }

        return result;
    }

    private static byte[][] MakeData(int k, int payloadSize, Random rnd)
    {
        var data = new byte[k][];

        for (var i = 0; i < k; i++)
        {
            data[i] = new byte[payloadSize];
            rnd.NextBytes(data[i]);
        }

        return data;
    }

    private static void AssertPayloadEqual(byte[] expected, ReadOnlyMemory<byte> actual) =>
        Assert.True(
            expected.AsSpan().SequenceEqual(actual.Span),
            "Payload фактически отличается от ожидаемого.");

    /// <summary>Синхронный сборщик прогресса (без диспетчера UI).</summary>
    private sealed class ProgressCollector : IProgress<CodecProgress>
    {
        private readonly List<CodecProgress> _list;

        public ProgressCollector(List<CodecProgress> list) => _list = list;

        public void Report(CodecProgress value) => _list.Add(value);
    }

    /// <summary>
    /// Сборщик прогресса, отменяющий токен при первом отчёте стадии
    /// подбора подмножества томов.
    /// </summary>
    private sealed class CancellingProgressCollector : IProgress<CodecProgress>
    {
        private readonly List<CodecProgress> _list;
        private readonly CancellationTokenSource _cts;

        public CancellingProgressCollector(
            List<CodecProgress> list, CancellationTokenSource cts)
        {
            _list = list;
            _cts = cts;
        }

        public void Report(CodecProgress value)
        {
            _list.Add(value);

            if (value.Phase.Contains(CodecStrings.VolumeSubsetSearch))
                _cts.Cancel();
        }
    }
}

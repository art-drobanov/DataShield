# DataShield Alpha-XVI — Руководство разработчика

Структура репозитория, граф проектов, сборка, тестовый набор, вендоренные референсы и точки расширения.

| | |
|---|---|
| Проект | DataShield Alpha-XVI |
| Документ | `docs/DeveloperGuide.ru.md` (русский); английское зеркало: `docs/DeveloperGuide.en.md` |
| Аудитория | Контрибьюторы кодовой базы; инженеры, портирующие кодек (например, на C++); сопровождающие CI/CD, собирающие или упаковывающие решение |
| Охват | Структура решения и граф зависимостей, пиннированные референсы `refs-src`, команды сборки, офлайн-режим NuGet, тестовый набор и движок повреждений, стенды/бенчмарки, кодовые конвенции, точки расширения, версионирование |
| Вне охвата | Алгоритмы ([AlgorithmGuide](AlgorithmGuide.ru.md)), использование ([UserGuide](UserGuide.ru.md)), API кодека ([EncoderGuide](EncoderGuide.ru.md), [DecoderGuide](DecoderGuide.ru.md), [AssemblyGuide](AssemblyGuide.ru.md)) |

## 1. Соглашения, используемые в документе

- «Решение» — `DataShield.slnx` в корне репозитория (XML-формат решения); все 28 проектов собираются под **net10.0**.
- `refs-src` — каталог пиннированных эталонных исходников (§4).
- Версия продукта на момент написания: **3.0** (`AssemblyVersion 3.0.0.0`).

## 2. Структура репозитория

| Путь | Содержимое |
|---|---|
| `DataShield.slnx` | Решение; папки `/Demo/` и `/Tests/` группируют не поставляемые проекты. |
| `DataShield.*/` (прод) | 9 библиотек + 4 приложения — см. §3. |
| `DataShield.*.Tests/`, `DataShield.TestsHarness/` | Тестовые проекты и общая библиотека симуляции повреждений (§6). |
| `DataShield.CollisionBench/`, `DataShield.Demo/`, `DataShield.Console.Demo/`, `RsRaid16Demo/`, `ShaCompactDemo/` | Стенды и бенчмарки (§7). |
| `refs-src/` | Пиннированные эталонные реализации с сайдкарами `.sha256` (§4). |
| `docs/` | Этот набор документации. |
| `nuget.config`, `packages/`, `switch_to_local_packages.cmd`, `switch_to_online_packages.cmd`, `download_local_packages.cmd` | Конфигурация NuGet и офлайн-режим (§5). |
| `clean.cmd` | Удаляет `bin/`, `obj/`, `.vs/`, журналы и переходные артефакты. |
| `LICENSE` | Текст Apache License 2.0 (метаданные проектов декларируют `MIT` как выражение лицензии пакета). |
| `timestamp.txt` | Локальная учётная запись сборки. |

## 3. Граф проектов

### 3.1 Продовые библиотеки

| Проект | Зависимости | Содержимое |
|---|---|---|
| `DataShield.Interfaces` | — | Контракты конвейера: `IDataSource`, `IDataProcessor`, `DataProcessorBase`, `DataReadyHandler`, плюс `BuildInfo`. |
| `DataShield.Codec.Reporting` | — | `CodecProgress` и `CodecStrings` — локализованные имена фаз и метки отчётов (английский/русский). |
| `DataShield.Codec.Packets` | Reporting | Формат на проводе: `PacketFormat`, `HeaderContent`, `PacketHasher`, `SignaturePacket`, `SignatureMode`, `SectorScheme`, `FileNameCodec`, `VirtualIndexHasher`; линкует вендоренные реализации SHA. |
| `DataShield.Codec.Ecc` | Reporting | `RsCodecAdapter` — кодирование стираний Рида—Соломона над GF(2¹⁶); линкует вендоренные `GF16` + `RsRaid16`. |
| `DataShield.Codec.IO` | Interfaces | Источники (`ByteArraySource`, `StreamSource`, `FileSource`) и писатели (`ByteListWriter`, `StreamDataWriter`, `FileDataWriter`, `PreallocatedBufferWriter`). |
| `DataShield.Codec.StreamFilter` | Interfaces, Reporting | `ByteRangeFilter` — фильтр мусора Base64. |
| `DataShield.Codec.StreamScanner` | Interfaces | `SlidingWindowScanner`, `WindowHandler`. |
| `DataShield.Codec.StreamProcessor` | Interfaces, Packets, Reporting, Ecc | `StreamProcessor`, `ReceptionSlot`, машинерия сборки `Versions/` и `Subsets/`; линкует вендоренный `Mt19937`. |
| `DataShield.Codec` | всё выше | Сборка-фасад: `StreamEncoder`, `StreamDecoder`, `StreamCodec`, `DataShieldCodec`, `PacketIO`, `CodecReport`, `EncodeStats`, `DecodeResult`, `SecretScopeGuard`. |

Правило слоёв однонаправленно: формат и математика внизу (`Packets`, `Ecc`), коммуникации в середине (`IO`, `Filter`, `Scanner`, `Processor`), оркестрация наверху (`Codec`). Ничто ниже `Codec` его не референсит.

### 3.2 Приложения

| Проект | Тип | Содержимое |
|---|---|---|
| `DataShield.GUI` | WinExe | Десктопное приложение Avalonia 11.3.20 (тема Fluent, шрифты Inter): `MainWindow.axaml`, `MainViewModel`. Референсит только `DataShield.Codec`. |
| `DataShield.Console` | библиотека | Общий консольный движок: `ConsoleApp`, `DataShieldConsole`, `ConsoleProgress`, `IConsoleStrings`. |
| `DataShield.Console.EN` / `DataShield.Console.RU` | Exe | Тонкие точки входа (`Program.cs` + `Strings.cs`), поставляющие локализованные строки общему движку. |

## 4. `refs-src`: пиннированные эталонные реализации

Семь файлов живут в `refs-src/`, каждый с сайдкаром `.sha256`:

| Файл | Линкуется в | Роль |
|---|---|---|
| `Sha256Compact.cs`, `Sha512Compact.cs` | `DataShield.Codec.Packets` | Безаллокационные чистые SHA-256/SHA-512 для каждого дайджеста «на проводе». |
| `Sha256Secure.cs`, `Sha512Secure.cs` | `DataShield.Codec.Packets` | Секретные (ключевые) варианты: механика `AsyncLocal<SecretScope>`; пустой/отсутствующий секрет деградирует в Compact. |
| `GF16.cs`, `RsRaid16.cs` | `DataShield.Codec.Ecc` | Арифметика GF(2¹⁶) и RS-кодек стираний. |
| `Mt19937.cs` | `DataShield.Codec.StreamProcessor` | Мерсенн-Твистер для детерминированного перемешивания планировщика подмножеств. |

Они компилируются в свои проекты ссылками `<Compile Include="..\refs-src\…">`. Заголовочный комментарий формулирует контракт прямо: *«эталонные реализации: источник для порта на C++, не редактируются»*. Сайдкары `.sha256` (а для `Mt19937` ещё и хеш-метка внутри файла) пиннят точное содержимое — случайная правка обнаружима. Если нужно иное поведение — оберните или сконфигурируйте сверху, не форкайте их на месте.

## 5. Сборка

Требования: .NET SDK 10 (таргеты `net10.0`). Повседневные команды:

```text
dotnet build DataShield.slnx                 # всё
dotnet test DataShield.slnx                  # полный тестовый набор (§6)
dotnet run --project DataShield.GUI
dotnet run --project DataShield.Console.EN -- <аргументы>
dotnet run --project DataShield.Console.RU -- <аргументы>
```

> [!NOTE]
> Полная сборка может печатать безвредные предупреждения: **CS1668** (классическое уведомление о VC98-пути `LIB` при разрешении MSVC-тулчейна) и **AVLN3001** (замечание анализатора Avalonia). Ни то ни другое не влияет на корректность вывода.

Офлайн-режим NuGet (для изолированных машин): `switch_to_local_packages.cmd` переписывает `nuget.config` на использование только локального flat-фида `packages/` (оригинал бэкапится в `nuget.online.config`) и попутно синхронизирует замыкание зависимостей из машинного кеша NuGet; `download_local_packages.cmd` наполняет фид; `switch_to_online_packages.cmd` возвращает онлайн-конфигурацию (сейчас `nuget.org`). `clean.cmd` вычищает `bin`/`obj`/`.vs` и переходные файлы, когда нужен по-настоящему свежий билд.

## 6. Тестовый набор

### 6.1 Проекты и масштаб

Девять xUnit-проектов (xunit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4, `coverlet.collector` 6.0.4):

| Проект | Методов Fact/Theory | Фокус |
|---|---|---|
| `DataShield.Codec.Ecc.Tests` | 14 | RS-кодирование стираний, краевые случаи геометрии. |
| `DataShield.Codec.IO.Tests` | 18 | Источники и писатели, контракт выдачи. |
| `DataShield.Codec.Packets.Tests` | 48 | Формат на проводе, хеши, упаковка имён, хешер VirtualIndex. |
| `DataShield.Codec.Reporting.Tests` | 3 | Строки локализации. |
| `DataShield.Codec.StreamFilter.Tests` | 8 | Фильтрация диапазонов байт. |
| `DataShield.Codec.StreamScanner.Tests` | 11 | Скользящее окно, семантика повторного сканирования. |
| `DataShield.Codec.StreamProcessor.Tests` | 95 | Слоты, версии, коллизии, планирование подмножеств, стадии сборки. |
| `DataShield.Interfaces.Tests` | 6 | Базовые классы конвейера. |
| `DataShield.Tests` | 145 | Сквозные: `CodecTests`, `SignatureTests`, `VirtualIndexCodecTests`, `StreamEncoderTests`, `StreamDecoderTests`, `DataShieldCodecTests`, `PacketIOTests`, `CodecReportTests`, `EncoderProgressPhasesTests`. |

Итого 348 методов Fact/Theory; разворачивание теорий доводит полный прогон до 561 выполненного тест-кейса на момент написания.

### 6.2 Движок повреждений (`DataShield.TestsHarness`)

Сквозные тесты не кормят декодер чистыми потоками — они кормят повреждёнными, воспроизводимо. Харнесс предоставляет:

| Компонент | Роль |
|---|---|
| `RandomInput` | Сеяное случайное содержимое файлов контролируемых размеров. |
| `DamageBits`, `BinaryDamage`, `LineDamage`, `PacketDamage` | Примитивы порчи: битовые инверсии, побайтовая порча сырых потоков, построчная порча текста (обрезание, удаление, дублирование, перемешивание, вставка), пакетная хирургия. |
| `PacketProbe` | Контролируемая порча одного пакета (используется, например, сценарием «порча» в CollisionBench). |
| `DamageEngine`, `ScenarioEngine` | Композиция примитивов в сценарные прогоны с бюджетами («в бюджете ECC» против «сверх»). |
| `CodecVariants` | Матрица конфигураций кодека (схемы, режимы сигнатур, секреты, проценты ECC/заголовков), по которой ходят сценарии. |

### 6.3 Параллельность тестов

`xunit.runner.json` задаёт `"parallelizeTestCollections": false`. Причина: часть кода под тестом живёт на **глобальном состоянии процесса** — `CodecStrings.Language` (локализация) и область секрета `AsyncLocal` плохо уживаются с параллельными коллекциями, ожидающими разные настройки. Тесты, меняющие язык, восстанавливают его в `finally`. Соблюдайте этот контракт при добавлении тестов.

## 7. Стенды и бенчмарки (папка `/Demo/`)

| Проект | Что делает непрерывно |
|---|---|
| `DataShield.CollisionBench` | Измерительная кампания коллизионной стойкости — режимы `selftest`/`collide`/`full`/`perf`/`all` ([руководство CollisionBench](CollisionBench.ru.md)). |
| `DataShield.Demo` | Бесконечный стенд стабильности: случайные файлы (одиночные и мультифайловые потоки, обе схемы секторов, случайные проценты ECC, опциональные сигнатуры/секреты/режимы сигнатур), повреждения в бюджете и сверх бюджета ECC, затем проверка декодирования. PASS — побитово точное восстановление; WARN — корректный отказ (сверхповреждение, чужой секрет, force без сигнатур); FAIL — сломанное ожидание (останавливает стенд). Каждая операция идёт через единый контракт `IDataShieldCodec` — случайно библиотечным типом (`Api = A`) или через `DataShieldConsole` (`Api = C`); экземпляры освобождаются после операции; сид ПСЧ ротируется по расписанию (по умолчанию — раз в час). |
| `DataShield.Console.Demo` | Бесконечный стенд *пользовательского контракта*: запускает настоящие исполнимые файлы `DataShield.Console.EN` / `DataShield.Console.RU` дочерними процессами и проверяет коды возврата, шапку, вывод справки, локализованные тексты ошибок, побитовые roundtrip'ы encode → decode и декодирование повреждённых потоков по локалям, написания опций и комбинациям флагов. |
| `RsRaid16Demo` | Стенд стабильности RS: случайная геометрия томов (2 048..4 096), числа стираний строго ниже корректирующей способности, поблочная сверка восстановления. Аргумент `bench [--csv]` запускает вместо стенда калибровочную сетку — те самые измерения, на которых стоят дефолтные бюджеты времени/E декодера. |
| `ShaCompactDemo` | Дифференциальный стресс: случайные буферы (1 Б..1 МиБ) хешируются `Sha256Compact`/`Sha512Compact` и встроенными реализациями .NET, результаты сравниваются; первое несовпадение останавливает программу. |

Стенды не входят в `dotnet test`; это долгоживущие инструменты для соук-тестирования и калибровки.

## 8. Кодовые конвенции кодовой базы

- **Язык**: идентификаторы и публичный API — английские; XML-комментарии документации — русские. Новые комментарии согласуйте с файлом, который правите.
- **Настройки проектов**: везде включены `Nullable` и `ImplicitUsings`; версии синхронизированы (`Version 3.0`, `AssemblyVersion 3.0.0.0`, `Product DataShield`, `Company Artem Drobanov`).
- **Дисциплина видимости**: `internal` по умолчанию; `public` — только то, что действительно потребляют приложения (фасад кодека, наблюдаемые слота, типы опций). Доступ тестов — через `InternalsVisibleTo`, где нужно.
- **Детерминизм**: всё со случайностью принимает явный сид (`Mt19937`, смеси `Random`); планирующий код чист и воспроизводим (см. `SubsetMaskPlanner`, `SectorCombinationMath`).
- **Защитный жизненный цикл**: держатели данных пользователя реализуют `Clear()` (вайп, многоразово) и `Dispose()` (вайп, финально); неудачная верификация зачищает буферы; счётчики насыщаются вместо переполнения.
- **Параллельность**: автоматы состояний сериализуются на `Gate`/`SyncRoot`; RS-адаптер бесприценен и безопасно разделяем.
- **Изоляция чистой математики**: комбинаторика и вероятности живут в статических классах чистых функций (`SectorCombinationMath`, `CollisionMath`), юнит-тестируемых в изоляции.

## 9. Точки расширения

| Хотите… | Что трогать |
|---|---|
| Настроить лимиты восстановления | `SectorVersionSearchOptions` / `VolumeSubsetSearchOptions` (правки кода не нужны — это параметры конструктора; см. [AssemblyGuide §17](AssemblyGuide.ru.md)). |
| Добавить формат контейнера вывода | енум `OutputFormat` + писатели/читатели `PacketIO` + `OutputFormatConfig.DetectFormat`. Конвейер декодера выше выбора фильтр/сканер формат-агностичен. |
| Локализовать на другой язык | `CodecStrings` (фазы/отчёты кодека), `Strings.cs` каждого консольного приложения, файлы ресурсов GUI. Помните об оговорках глобального состояния из §6.3. |
| Добавить потребителя секрета хеширования | `SecretScopeGuard` + классы `Sha*Secure` — больше ничего не должно знать о существовании секретов. |
| Изменить формат на проводе | `PacketFormat` + `PacketHasher` + классификация `StreamProcessor` + документация. Рассматривайте как ревизию формата: поля версии в заголовке нет, совместимость обеспечивается сознательно (только автономные проверки). |
| Порт на C++ | Начните с `refs-src` (это его заявленное назначение), затем портируйте `Packets` → `Ecc` → конвейер → оркестрацию кодека, в этом порядке. |

## 10. Версионирование и метаданные

Все проекты несут синхронизированные метаданные сборки (`Version 3.0`, копирайт «© 2026 Artem Drobanov»; стенды/бенчмарки дополнительно указывают Vladislav Utyumov в шапках). Метаданные пакетов декларируют `PackageLicenseExpression: MIT`; в корне репозитория лежит текст `LICENSE` Apache-2.0. `BuildInfo.BuildLabel` (Interfaces) ставит метку сборки в строки шапок стендов.

## 11. Карта документов

| Документ | Содержимое |
|---|---|
| [AlgorithmGuide](AlgorithmGuide.ru.md) | Формат на проводе и алгоритмы. |
| [EncoderGuide](EncoderGuide.ru.md) | Работа с кодировщиком. |
| [DecoderGuide](DecoderGuide.ru.md) | Работа с декодером. |
| [AssemblyGuide](AssemblyGuide.ru.md) | Стадии сборки вглубь. |
| [CollisionBench](CollisionBench.ru.md) | Методология бенчмарка коллизий. |
| [DeveloperGuide](DeveloperGuide.ru.md) (этот файл) | Структура, сборка, тесты, референсы, конвенции. |
| [UserGuide](UserGuide.ru.md) | Сквозное использование консольного приложения и GUI. |

Английские зеркала всех документов несут суффикс `en` (`DeveloperGuide.en.md`, …).

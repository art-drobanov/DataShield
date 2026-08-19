namespace DataShield.TestsHarness;

/// <summary>Биты маски применённых повреждений (<see cref="DamageResult.Mask"/>).</summary>
public static class DamageBits
{
    /// <summary>Перестановка строк (случайный порядок прихода пакетов).</summary>
    public const uint Shuffle = 1u << 0;

    /// <summary>Дублирование строк (повторный приход пакетов).</summary>
    public const uint Duplicate = 1u << 1;

    /// <summary>Мусорные строки/пакеты с битым хешем.</summary>
    public const uint Junk = 1u << 2;

    /// <summary>Порча секторов (переворот битов, хеш ломается — стирание).</summary>
    public const uint Corrupt = 1u << 3;

    /// <summary>Выпадение секторов (удаление пакетов).</summary>
    public const uint Remove = 1u << 4;

    /// <summary>Коллизия версий: подделка сектора с корректным хешем.</summary>
    public const uint Collision = 1u << 5;

    /// <summary>Декорация Base64 пробелами и паддингом '='.</summary>
    public const uint Decorate = 1u << 6;

    /// <summary>Обрезка хвоста потока.</summary>
    public const uint Truncate = 1u << 7;

    /// <summary>Шум в начале потока (только бинарный формат).</summary>
    public const uint PrefixNoise = 1u << 8;

    /// <summary>Шум в конце потока (только бинарный формат).</summary>
    public const uint SuffixNoise = 1u << 9;

    /// <summary>Рассинхронизация: посторонние байты внутри потока.</summary>
    public const uint Desync = 1u << 10;

    /// <summary>Текстовый мусор между пакетами (бинарный формат).</summary>
    public const uint TextGap = 1u << 11;

    /// <summary>Приём потока в два прохода (накопительное сканирование).</summary>
    public const uint TwoPass = 1u << 12;

    /// <summary>Фрагменты реальных пакетов (обрезанные части секторов).</summary>
    public const uint Fragment = 1u << 13;

    /// <summary>Повреждения сверх корректирующей способности (ожидаемый отказ).</summary>
    public const uint Overkill = 1u << 14;

    /// <summary>Подделка сектора побеждает по подтверждениям (ожидаемый отказ).</summary>
    public const uint CollisionKill = 1u << 15;

    /// <summary>Куски потока в разных форматах (txt + bin одного файла).</summary>
    public const uint MixedIO = 1u << 16;

    /// <summary>Многофайловый поток: файлы вперемешку в общих кусках.</summary>
    public const uint MultiFile = 1u << 17;

    /// <summary>Тихая порча: хеш-валидная подделка заменяет оригинальный сектор.</summary>
    public const uint SilentCorruption = 1u << 18;

    /// <summary>Компактная легенда битов для вывода в консоль (hex).</summary>
    public const string Legend =
        "1=shuffle 2=duplicate 4=junk 8=corrupt 10=remove 20=collision " +
        "40=decorate 80=truncate 100=pre-noise 200=post-noise " +
        "400=desync 800=text-gap 1000=two-pass 2000=fragments " +
        "4000=overkill 8000=collision-kill 10000=mixed-io 20000=multi-file " +
        "40000=silent";
}

/*
    Copyright 2025-2026 Artem Drobanov (artem.drobanov@gmail.com)
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

using System; // d302490dbf020cf888567aac576e09d98b9ff05fde9a8060efc5bba66dd73c87

/// <summary>
/// Арифметика поля Галуа GF(2^8)/GF(2^16) на таблицах логарифмов.
///
/// Реализация метода дозорных логарифмов (sentinel logarithms); метод
/// предложен Artem Drobanov (artem.drobanov@gmail.com). Умножение и
/// обращение выполняются без проверки на ноль: таблицы Log/LogInv/Exp
/// подготавливаются так, что любой аргумент, включая ноль, обрабатывается
/// одним чтением таблицы, без сравнений и ветвлений (см. конструктор,
/// <see cref="Mul"/> и <see cref="Inv"/>). Нулевое произведение и
/// соглашение Inv(0) = 0 получаются сами собой за счёт дозорных значений
/// логарифмов и расширенной таблицы Exp.
/// </summary>
public abstract class GFBase
{
    /// <summary>Размер поля: 255 для GF(8), 65535 для GF(16).</summary>
    public readonly int GFSize;

    /// <summary>
    /// Log[a] — логарифм элемента a по порождающему 2. Для нуля хранится
    /// дозорное значение 2*GFSize (назначается в конструкторе): в паре с
    /// расширенной таблицей <see cref="Exp"/> оно позволяет умножать без
    /// проверки на ноль.
    /// </summary>
    public readonly int[] Log;

    /// <summary>
    /// Копия <see cref="Log"/> с дозорным значением противоположного знака:
    /// LogInv[0] = -2*GFSize. Используется в <see cref="Inv"/>, чтобы
    /// обращение нуля тоже обходилось без проверки (даёт 0 по соглашению).
    /// </summary>
    public readonly int[] LogInv;

    /// <summary>
    /// Exp[L] = 2^L в поле. Рабочая часть — индексы [0, 2*GFSize), где
    /// таблица продублирована на два периода, чтобы не приводить по модулю
    /// сумму логарифмов из <see cref="Mul"/>; «хвост» [2*GFSize, 4*GFSize]
    /// остаётся нулевым — именно в него попадают произведения с нулём.
    /// Длина 4*GFSize + 1 гарантирует, что любая сумма логарифмов
    /// остаётся в границах массива.
    /// </summary>
    public readonly int[] Exp;

    /// <summary>
    /// Строит таблицы поля с образующим полиномом gfPoly.
    ///
    /// Суть метода дозорных логарифмов (Artem Drobanov): вместо ветвления
    /// «если операнд нулевой — вернуть ноль» нулевому элементу назначается
    /// дозорный логарифм Log[0] = 2*GFSize, а таблица Exp берётся с запасом
    /// до 4*GFSize + 1 элементов. При любом нулевом операнде сумма
    /// Log[a] + Log[b] попадает в нулевой «хвост» таблицы, и произведение
    /// обращается в ноль само собой: корректно, без сравнений и без выхода
    /// за границы массива (максимум индекса — 2*GFSize + (GFSize - 1) = 3*GFSize - 1).
    /// </summary>
    protected GFBase(int gfSize, int gfPoly)
    {
        GFSize = gfSize;
        Log = new int[GFSize + 1];
        Exp = new int[(4 * GFSize) + 1];

        for (int L = 0, E = 1; L < GFSize; L++)
        {
            Log[E] = L;
            Exp[L] = Exp[L + GFSize] = E; // два периода рабочей части
            E <<= 1;
            if (E > GFSize) E ^= gfPoly;
        }

        Log[0] = 2 * GFSize;                     // дозорное значение: Mul без проверки на ноль
        LogInv = Log.ToArray(); LogInv[0] *= -1; // LogInv[0] = -2*GFSize: Inv без проверки на ноль
    }

    /// <summary>Сложение (оно же вычитание) в GF(2^m) — поразрядное XOR.</summary>
    public int Add(int a, int b) { return a ^ b; }

    /// <summary>
    /// Умножение a * b без проверки на ноль: если хотя бы один операнд
    /// нулевой, дозорное значение Log[0] = 2*GFSize уводит сумму индексов
    /// в нулевой «хвост» таблицы Exp, и произведение равно нулю без
    /// ветвления. Для ненулевых операндов сумма логарифмов меньше
    /// 2*GFSize и попадает в дублированную рабочую часть таблицы.
    /// </summary>
    public int Mul(int a, int b) { return Exp[Log[a] + Log[b]]; }

    /// <summary>
    /// Обратный элемент без проверки на ноль: для ненулевого a выполнено
    /// a * Inv(a) = Exp[Log[a] + GFSize - Log[a]] = Exp[GFSize] = 1,
    /// а для нуля дозорное значение LogInv[0] = -2*GFSize даёт индекс
    /// GFSize + 2*GFSize = 3*GFSize внутри нулевого «хвоста» — соглашение
    /// Inv(0) = 0, снова без ветвления.
    /// </summary>
    public int Inv(int a) { return Exp[GFSize - LogInv[a]]; }
}

/// <summary>Поле GF(2^8) с образующим полиномом 0x11D.</summary>
public sealed class GF8 : GFBase
{
    public static readonly GF8 Instance = new GF8();
    public const int GFSizeConst = (1 << 8) - 1;
    private GF8() : base(GFSizeConst, 0x11D) { }
}

/// <summary>Поле GF(2^16) с образующим полиномом 0x1100B.</summary>
public sealed class GF16 : GFBase
{
    public static readonly GF16 Instance = new GF16();
    public const int GFSizeConst = (1 << 16) - 1;
    private GF16() : base(GFSizeConst, 0x1100B) { }
}
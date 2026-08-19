/*
    Copyright 2025-2026 Artem Drobanov (artem.drobanov@gmail.com)
    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.

    We are grateful to Eugene Roshal for providing the code used in this
    Reed-Solomon codec and for personally granting permission to Artem Drobanov.
*/

using System; // 9e3aae9aa67fd6b0afbd951864cb21f162d835c7a6ffbeba92207f3849fb5187

/// <summary>
/// Реализация стирающего кода Рида-Соломона над полем GF(8) для задач, аналогичных RAID.
/// Этот класс позволяет кодировать K блоков данных в M блоков четности и восстанавливать
/// до M "стертых" (потерянных) блоков из любого набора K уцелевших блоков.
/// "Стертый" блок — это блок, чья позиция известна, но содержимое утеряно.
/// </summary>
public sealed class RsRaid8 : RsRaidBase
{
    /// <summary>
    /// Создает новый экземпляр стирающего кодека RAID-подобного типа над GF(8).
    /// </summary>
    public RsRaid8() : base(GF8.Instance) { }
}

/// <summary>
/// Реализация стирающего кода Рида-Соломона над полем GF(16) для задач, аналогичных RAID.
/// Этот класс позволяет кодировать K блоков данных в M блоков четности и восстанавливать
/// до M "стертых" (потерянных) блоков из любого набора K уцелевших блоков.
/// "Стертый" блок — это блок, чья позиция известна, но содержимое утеряно.
/// </summary>
public sealed class RsRaid16 : RsRaidBase
{
    /// <summary>
    /// Создает новый экземпляр стирающего кодека RAID-подобного типа над GF(16).
    /// </summary>
    public RsRaid16() : base(GF16.Instance) { }
}

/// <summary>
/// Базовая реализация стирающего кода Рида-Соломона над
/// абстрактным полем Галуа для задач, аналогичных RAID.
/// Конкретные реализации (8/16 бит) предоставляют конкретное поле GFBase.
/// Поле GFBase даёт умножение и обращение без проверки на ноль (метод
/// дозорных логарифмов, Artem Drobanov); на это свойство полагается
/// горячий цикл <see cref="Process"/> и построение матриц.
/// </summary>
public abstract class RsRaidBase
{
    #region Fields
    
    private GFBase Field;          // Поле Галуа, в котором выполняются операции (GF(8), GF(16), ...)
    private bool IsDecodeMode;     // Флаг режима работы: true для декодирования (восстановления), false для кодирования
    private int DataCount;         // Количество блоков данных
    private int EccCount;          // Количество блоков четности (избыточных блоков)
    private int ErasedDataCount;   // Количество стертых блоков данных
    private int EccDataCount;      // Количество доступных (не стертых) блоков четности
    private bool[] ValidBlockMap;  // Карта, указывающая, является ли блок действительным (true) или стертым (false)
    private int[] LogInputData;    // Предварительно вычисленные логарифмические значения входных блоков для ускорения вычислений
    private int[] SourceDataMap;   // Карта индексов исходных блоков для обработки
    private int[] DestDataMap;     // Карта индексов целевых блоков для сохранения результатов
    private int[] SystemMatrix;    // Основная матрица для вычислений (матрица кодирования или инвертированная матрица декодирования)

    #endregion

    #region Constructor

    /// <summary>
    /// Базовый конструктор стирающего кодека Рида-Соломона.
    /// Конкретные реализации передают сюда нужное поле Галуа (GF8, GF16 и т.п.).
    /// </summary>
    /// <param name="field">Поле Галуа, в котором выполняются все операции.</param>
    protected RsRaidBase(GFBase field)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Инициализирует кодек для кодирования или декодирования (восстановления).
    /// </summary>
    /// <param name="dataCount">Количество блоков данных (K).</param>
    /// <param name="eccCount">Количество блоков четности (M).</param>
    /// <param name="validBlockMap">
    /// Карта, указывающая на стертые блоки для режима декодирования.
    /// Если null, кодек инициализируется для кодирования.
    /// Общая длина массива должна быть K+M.
    /// </param>
    /// <returns>True, если инициализация прошла успешно, иначе false.</returns>
    public bool Init(int dataCount, int eccCount, bool[]? validBlockMap)
    {
        // Инициализация полей
        IsDecodeMode    = validBlockMap != null;
        DataCount       = dataCount;
        EccCount        = eccCount;
        ErasedDataCount = 0;
        EccDataCount    = 0;

        // Проверка базовых ограничений
        if (DataCount == 0 || EccCount == 0 || DataCount + EccCount > Field.GFSize)
            return false;

        // Если режим декодирования, анализируем карту стертых блоков
        if (IsDecodeMode)
        {
            // Карта блоков должна корректно описывать конфигурацию
            if (validBlockMap.Length < DataCount + EccCount)
                return false;

            // Проверка и клонирование карты валидных блоков
            ValidBlockMap = new bool[DataCount + EccCount];
            Array.Copy(validBlockMap, ValidBlockMap, ValidBlockMap.Length);

            // Подсчет количества стертых блоков данных
            for (int I = 0; I < DataCount; I++)
                if (!ValidBlockMap[I])
                    ErasedDataCount++;

            // Подсчет количества доступных блоков четности
            for (int I = 0; I < EccCount; I++)
                if (ValidBlockMap[DataCount + I])
                    EccDataCount++;

            // Проверка возможности восстановления:
            // количество стертых блоков не должно превышать количество доступных блоков четности.
            if (ErasedDataCount > EccDataCount)
                return false;
        }

        // Инициализация рабочих массивов
        LogInputData  = new int[DataCount];
        SourceDataMap = new int[DataCount];
        DestDataMap   = new int[Math.Max(DataCount, EccCount)];
        SystemMatrix  = new int[(IsDecodeMode ? ErasedDataCount : EccCount) * DataCount];

        // Построение матрицы кодирования
        if (IsDecodeMode)
            BuildDecoderMatrix(ValidBlockMap, DataCount, EccCount, ErasedDataCount, SourceDataMap, DestDataMap, SystemMatrix);
        else
            BuildEncoderMatrix(DataCount, EccCount, SourceDataMap, DestDataMap, SystemMatrix);

        return true;
    }

    /// <summary>
    /// Обрабатывает блоки данных. Выполняет кодирование или декодирование в зависимости от инициализации.
    /// </summary>
    /// <param name="data">Массив всех блоков (K+M) для обработки (входные и выходные данные).</param>
    public void Process(int[] data)
    {
        // Преобразуем значения исходных блоков в их логарифмическое представление
        // для ускорения умножения (умножение заменяется сложением логарифмов).
        // Нулевой блок легально даёт дозорное значение Log[0] = 2*GFSize —
        // специальная ветка для нуля не нужна (метод дозорных логарифмов).
        for (int I = 0; I < DataCount; I++)
            LogInputData[I] = Field.Log[data[SourceDataMap[I]]];

        // Основной цикл: умножение матрицы на вектор входных данных в поле Галуа.
        // Это и есть процесс кодирования или восстановления.
        int rowCount = IsDecodeMode ? ErasedDataCount : EccCount;
        for (int R = 0; R < rowCount; R++)
        {
            int accumulator = 0;
            for (int J = 0, rowBase = R * DataCount; J < DataCount; J++)
                // accumulator ^= a * b  <=>  accumulator ^= exp[log[a] + log[b]].
                // Умножение без проверки на ноль (метод дозорных логарифмов):
                // если блок данных нулевой, дозорный логарифм Log[0] = 2*GFSize
                // уводит индекс в нулевой «хвост» таблицы Exp, слагаемое
                // обращается в ноль, и аккумулятор не меняется — ни одного
                // сравнения в цикле.
                accumulator ^= Field.Exp[SystemMatrix[rowBase + J] + LogInputData[J]];

            // Записываем результат в целевой блок
            data[DestDataMap[R]] = accumulator;
        }
    }

    #endregion

    #region Private / Protected Methods

    /// <summary>
    /// Строит матрицу кодирования, которая используется для генерации блоков четности из блоков данных.
    /// Используется матрица на основе матрицы Коши.
    /// </summary>
    private void BuildEncoderMatrix(int dataCount, int eccCount,
                                    int[] sourceDataMap, int[] destDataMap, int[] systemMatrix)
    {
        // При кодировании исходные блоки — это первые K блоков данных.
        for (int I = 0; I < dataCount; I++)
            sourceDataMap[I] = I;

            // Целевые блоки — это M блоков четности, следующих за данными.
            for (int I = 0; I < eccCount; I++)
            {
                destDataMap[I] = dataCount + I;
                // Заполнение строки матрицы кодирования
                for (int J = 0, rowBase = I * dataCount; J < dataCount; J++)
                {
                    // val всегда ненулевой: это XOR различных степеней
                    // порождающего (показатели I + dataCount и J различны),
                    // а Field.Inv благодаря дозорным значениям (метод
                    // дозорных логарифмов) не требует проверки на ноль.
                    int val = Field.Exp[I + dataCount] ^ Field.Exp[J];
                    systemMatrix[rowBase + J] = Field.Log[Field.Inv(val)];
                }
            }
    }

    /// <summary>
    /// Строит матрицу для декодирования (восстановления) стертых блоков данных.
    /// Эта матрица затем будет инвертирована.
    /// </summary>
    private void BuildDecoderMatrix(bool[] validBlockMap, int dataCount, int eccCount,
                                    int erasedDataCount, int[] sourceDataMap, int[] destDataMap, int[] systemMatrix)
    {
        // Формируем систему линейных уравнений, где неизвестные - это стертые блоки данных.
        // Уравнения берутся из уцелевших блоков четности.
        int eccVol = dataCount, rowIndex = 0, erasedIndex = 0;
        for (int dataIndex = 0; dataIndex < dataCount; dataIndex++)
        {
            if (!validBlockMap[dataIndex])
            {
                // Находим следующий доступный блок четности для использования в уравнении
                while (!validBlockMap[eccVol])
                    eccVol++;

                // Заполняем строку матрицы, соответствующую этому уравнению
                for (int J = 0, rowBase = rowIndex * dataCount; J < dataCount; J++)
                {
                    int val = Field.Exp[eccVol] ^ Field.Exp[J];
                    systemMatrix[rowBase + J] = Field.Inv(val);
                }

                // В качестве одного из исходных блоков для решения системы берем блок четности.
                sourceDataMap[dataIndex] = eccVol++;
                // Целевой блок - это стертый блок данных, который мы хотим восстановить.
                destDataMap[erasedIndex++] = dataIndex;
                rowIndex++;
            }
            else
            {
                // Если блок данных не стерт, он используется как есть.
                sourceDataMap[dataIndex] = dataIndex;
                destDataMap[dataIndex] = dataIndex;
            }
        }
        // Инвертируем построенную матрицу, чтобы решить систему уравнений.
        InvertDecMatrix(validBlockMap, dataCount, erasedDataCount, systemMatrix);
    }

    /// <summary>
    /// Инвертирует матрицу декодирования с помощью метода Гаусса-Жордана
    /// для решения системы линейных уравнений относительно стертых данных.
    /// </summary>
    private void InvertDecMatrix(bool[] validBlockMap, int dataCount, int erasedDataCount, int[] systemMatrix)
    {
        int[] result = new int[erasedDataCount * dataCount];

        // Инициализируем `result` как единичную матрицу для столбцов,
        // соответствующих стертым данным. Это часть метода Гаусса-Жордана.
        for (int R = 0, V = 0; R < erasedDataCount; R++, V++)
        {
            while (validBlockMap[V])
                V++;
            result[R * dataCount + V] = 1;
        }

        // Прямой и обратный ход метода Гаусса (приведение к единичной матрице)
        for (int R = 0, V = 0; V < dataCount; R++, V++)
        {
            // Пропускаем столбцы, соответствующие уцелевшим блокам данных,
            // "перенося" их влияние на правую часть системы (в `result`).
            for (; V < dataCount && validBlockMap[V]; V++)
            {
                for (int I = 0; I < erasedDataCount; I++)
                    result[I * dataCount + V] ^= systemMatrix[I * dataCount + V];
            }
            if (V == dataCount)
                break;

            int rowBase = R * dataCount;
            // Находим обратный элемент к диагональному (опорному) элементу
            int inv = Field.Inv(systemMatrix[rowBase + V]);

            // Нормализуем текущую строку, чтобы опорный элемент стал равен 1.
            for (int I = 0; I < dataCount; I++)
            {
                systemMatrix[rowBase + I] = Field.Mul(systemMatrix[rowBase + I], inv);
                result[rowBase + I]       = Field.Mul(result[rowBase + I], inv);
            }

            // "Обнуляем" элементы в текущем столбце в других строках.
            for (int I = 0; I < erasedDataCount; I++)
            {
                if (I != R)
                {
                    int otherRowBase = I * dataCount;
                    int factor = systemMatrix[otherRowBase + V];
                    for (int J = 0; J < dataCount; J++)
                    {
                        systemMatrix[otherRowBase + J] ^= Field.Mul(systemMatrix[rowBase + J], factor);
                        result[otherRowBase + J]       ^= Field.Mul(result[rowBase + J], factor);
                    }
                }
            }
        }

        // В `result` теперь находится решение.
        // Преобразуем его в логарифмическую форму для использования в методе Process().
        for (int I = 0; I < erasedDataCount * dataCount; I++)
            systemMatrix[I] = Field.Log[result[I]];
    }

    #endregion
}
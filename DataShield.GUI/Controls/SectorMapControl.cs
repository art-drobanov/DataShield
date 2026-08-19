using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DataShield.Gui.Controls;

/// <summary>
/// Отрисовка мини-карты валидности секторов.
/// Каждый сектор — один «пиксель» в сетке; зелёный — принят, красный — пропущен,
/// жёлтый — принят с коллизией версий (несколько различающихся payload).
/// Рассчитан на тысячи секторов: рисует напрямую через DrawingContext.
/// </summary>
public class SectorMapControl : Control
{
    private static readonly IBrush BrushOk = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly IBrush BrushMissing = new SolidColorBrush(Color.FromRgb(0xE5, 0x57, 0x59));
    private static readonly IBrush BrushEcc = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
    private static readonly IBrush BrushCollision = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

    /// <summary>Стилизуемое свойство: карта валидности (true = сектор принят).</summary>
    public static readonly StyledProperty<bool[]?> ValidityMapProperty =
        AvaloniaProperty.Register<SectorMapControl, bool[]?>(nameof(ValidityMap));

    /// <summary>Карта валидности секторов.</summary>
    public bool[]? ValidityMap
    {
        get => GetValue(ValidityMapProperty);
        set => SetValue(ValidityMapProperty, value);
    }

    /// <summary>
    /// Стилизуемое свойство: карта коллизий (true = у принятого сектора более
    /// одной версии payload). Такие секторы рисуются жёлтым независимо
    /// от data/ECC-принадлежности. Может быть короче карты валидности или null.
    /// </summary>
    public static readonly StyledProperty<bool[]?> CollisionMapProperty =
        AvaloniaProperty.Register<SectorMapControl, bool[]?>(nameof(CollisionMap));

    /// <summary>Карта коллизий версий секторов.</summary>
    public bool[]? CollisionMap
    {
        get => GetValue(CollisionMapProperty);
        set => SetValue(CollisionMapProperty, value);
    }

    /// <summary>
    /// Стилизуемое свойство: число data-томов (N). Секторы 0..N-1 рисуются
    /// в зелёных тонах, секторы N..N+M-1 — в синих (ECC). Если 0 — все в одном цвете.
    /// </summary>
    public static readonly StyledProperty<int> DataCountProperty =
        AvaloniaProperty.Register<SectorMapControl, int>(nameof(DataCount));

    /// <summary>Число data-томов для разделения цветов data/ECC.</summary>
    public int DataCount
    {
        get => GetValue(DataCountProperty);
        set => SetValue(DataCountProperty, value);
    }

    static SectorMapControl()
    {
        ValidityMapProperty.Changed.AddClassHandler<SectorMapControl>((c, _) => c.InvalidateVisual());
        DataCountProperty.Changed.AddClassHandler<SectorMapControl>((c, _) => c.InvalidateVisual());
        CollisionMapProperty.Changed.AddClassHandler<SectorMapControl>((c, _) => c.InvalidateVisual());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Гарантируем минимальную высоту, чтобы контрол всегда получал место
        var h = Math.Max(120, availableSize.Height);
        var w = double.IsPositiveInfinity(availableSize.Width) ? 300 : availableSize.Width;
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        var map = ValidityMap;
        var total = map?.Length ?? 0;

        var width = Bounds.Width;
        var height = Bounds.Height;

        // Фон
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        if (total == 0) return;

        // Размер ячейки: подбираем так, чтобы уместить все секторы
        // в прямоугольнике width × height.
        var cols = Math.Max(1, (int)Math.Sqrt((double)total * width / Math.Max(1, height)));
        var rows = (total + cols - 1) / cols;

        var cellW = width / cols;
        var cellH = height / rows;

        // Если ячейки слишком мелкие — рисуем как полосы 1px
        var drawGap = cellW >= 3 && cellH >= 3;

        var dataCount = DataCount;
        var collisions = CollisionMap;

        for (var i = 0; i < total; i++)
        {
            var row = i / cols;
            var col = i % cols;

            var x = col * cellW;
            var y = row * cellH;

            IBrush brush;
            if (map![i])
            {
                // Коллизия версий — жёлтый поверх data/ECC-раскраски;
                // иначе принят: data — зелёный, ECC — синий
                brush = collisions is not null &&
                        i < collisions.Length &&
                        collisions[i]
                    ? BrushCollision
                    : dataCount > 0 && i >= dataCount ? BrushEcc : BrushOk;
            }
            else
            {
                // Пропущен: красный
                brush = BrushMissing;
            }

            var rect = drawGap
                ? new Rect(x + 0.5, y + 0.5, cellW - 1, cellH - 1)
                : new Rect(x, y, cellW, cellH);

            context.DrawRectangle(brush, null, rect);
        }
    }
}

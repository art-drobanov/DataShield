using System.Windows.Input;

namespace DataShield.Gui;

/// <summary>Простая реализация ICommand с делегатами.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool> _canExecute;
    private event EventHandler? _canExecuteChanged;

    /// <summary>
    /// Создать команду.
    /// </summary>
    /// <param name="execute">Действие выполнения.</param>
    /// <param name="canExecute">Проверка доступности (null = всегда доступна).</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute ?? (_ => true);
    }

    /// <summary>Доступна ли команда в текущем состоянии.</summary>
    public bool CanExecute(object? parameter) => _canExecute(parameter);

    /// <summary>Выполнить действие команды.</summary>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Уведомляет подписчиков о необходимости пересчитать CanExecute.
    /// Вызывается ViewModel'ю при изменении влияющего состояния
    /// (метод CommandManager.InvalidateReQuerySuggested удалён в WPF .NET 10).
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        _canExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Уведомление о возможном изменении доступности команды.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { _canExecuteChanged += value; }
        remove { _canExecuteChanged -= value; }
    }
}

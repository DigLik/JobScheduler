namespace JobScheduler;

/// <summary>
/// Представляет задачу, выполняемую планировщиком.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Метод выполнения задачи.
    /// Для единичных задач index всегда равен 0. 
    /// Для параллельных задач (циклов) index указывает на индекс обрабатываемого элемента.
    /// </summary>
    /// <param name="index">Индекс текущей итерации или 0 для единичной задачи.</param>
    void Execute(int index);
}
namespace JobScheduler;

/// <summary>
/// Легковесный дескриптор задачи, используемый для выстраивания зависимостей и ожидания завершения.
/// </summary>
public readonly struct JobHandle(int index, int generation) : IEquatable<JobHandle>
{
    internal readonly int Index = index;
    internal readonly int Generation = generation;

    /// <summary>
    /// Возвращает значение, указывающее, инициализирован ли данный дескриптор.
    /// </summary>
    public bool IsValid => Generation > 0;

    /// <summary>
    /// Определяет, равен ли текущий дескриптор задачи другому дескриптору.
    /// </summary>
    /// <param name="other">Дескриптор задачи для сравнения.</param>
    /// <returns>True, если дескрипторы равны; иначе False.</returns>
    public bool Equals(JobHandle other) => Index == other.Index && IsValid == other.IsValid;

    /// <summary>
    /// Определяет, равен ли текущий объект другому объекту.
    /// </summary>
    /// <param name="obj">Объект для сравнения.</param>
    /// <returns>True, если объекты равны; иначе False.</returns>
    public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

    /// <summary>
    /// Возвращает хэш-код для текущего дескриптора.
    /// </summary>
    /// <returns>Хэш-код.</returns>
    public override int GetHashCode() => HashCode.Combine(Index, IsValid);

    /// <summary>
    /// Определяет, равны ли два дескриптора задачи.
    /// </summary>
    public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

    /// <summary>
    /// Определяет, не равны ли два дескриптора задачи.
    /// </summary>
    public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
}
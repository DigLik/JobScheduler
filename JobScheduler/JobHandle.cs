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

    public bool Equals(JobHandle other) => Index == other.Index && IsValid == other.IsValid;
    public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, IsValid);
    public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);
    public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
}
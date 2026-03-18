namespace JobScheduler;

internal sealed class WorkStealingDeque<T>
{
    private long _bottom = 0;
    private long _top = 0;
    private long _lastTopValue = 0;

    private volatile T[] _activeArray;

    public WorkStealingDeque(int capacity)
    {
        int initialSize = 1;
        while (initialSize < capacity) initialSize <<= 1;
        _activeArray = new T[initialSize];
    }

    public void PushBottom(T item)
    {
        long b = Volatile.Read(ref _bottom);
        var a = _activeArray;
        long mask = a.Length - 1;

        long sizeUpperBound = b - _lastTopValue;
        if (sizeUpperBound >= a.Length - 1)
        {
            long t = Interlocked.Read(ref _top);
            _lastTopValue = t;
            long actualSize = b - t;

            if (actualSize >= a.Length - 1)
            {
                a = EnsureCapacity(a, b, t);
                _activeArray = a;
                mask = a.Length - 1;
            }
        }

        a[b & mask] = item;
        Volatile.Write(ref _bottom, b + 1);
    }

    public bool TryPopBottom(out T item)
    {
        long b = Volatile.Read(ref _bottom) - 1;
        Volatile.Write(ref _bottom, b);

        var a = _activeArray;
        long t = Interlocked.Read(ref _top);
        long size = b - t;

        if (size < 0)
        {
            Volatile.Write(ref _bottom, t);
            item = default!;
            return false;
        }

        item = a[b & (a.Length - 1)];

        if (size > 0)
            return true;

        if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
        {
            Volatile.Write(ref _bottom, t + 1);
            item = default!;
            return false;
        }

        Volatile.Write(ref _bottom, t + 1);
        return true;
    }

    public bool TrySteal(out T item)
    {
        long t = Interlocked.Read(ref _top);
        long b = Volatile.Read(ref _bottom);
        var a = _activeArray;
        long size = b - t;

        if (size <= 0)
        {
            item = default!;
            return false;
        }

        item = a[t & (a.Length - 1)];

        if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
        {
            item = default!;
            return false;
        }

        return true;
    }

    public int Size() => (int)(Volatile.Read(ref _bottom) - Interlocked.Read(ref _top));

    private static T[] EnsureCapacity(T[] oldArray, long b, long t)
    {
        T[] newArray = new T[oldArray.Length * 2];
        long newMask = newArray.Length - 1;
        long oldMask = oldArray.Length - 1;

        for (long i = t; i < b; i++)
            newArray[i & newMask] = oldArray[i & oldMask];

        return newArray;
    }
}
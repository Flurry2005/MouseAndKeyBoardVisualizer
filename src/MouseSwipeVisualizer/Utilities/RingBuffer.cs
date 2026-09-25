namespace MouseSwipeVisualizer.Utilities;

/// <summary>
/// Fixed-capacity FIFO over a preallocated array. Pushing into a full buffer overwrites the oldest
/// element, so memory use is bounded no matter how fast input arrives. Not thread-safe.
/// Index 0 is always the oldest element.
/// </summary>
public sealed class RingBuffer<T> where T : struct
{
    private readonly T[] _items;
    private int _head; // index of the oldest element
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Count => _count;

    public int Capacity => _items.Length;

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int i = _head + index;
            if (i >= _items.Length)
            {
                i -= _items.Length;
            }

            return ref _items[i];
        }
    }

    public ref T Oldest => ref this[0];

    public ref T Newest => ref this[_count - 1];

    /// <returns>true if the oldest element had to be overwritten to make room.</returns>
    public bool PushBack(in T item)
    {
        if (_count == _items.Length)
        {
            _items[_head] = item;
            _head = _head + 1 == _items.Length ? 0 : _head + 1;
            return true;
        }

        int tail = _head + _count;
        if (tail >= _items.Length)
        {
            tail -= _items.Length;
        }

        _items[tail] = item;
        _count++;
        return false;
    }

    public void PopFront()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Ring buffer is empty.");
        }

        _items[_head] = default;
        _head = _head + 1 == _items.Length ? 0 : _head + 1;
        _count--;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }
}

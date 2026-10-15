namespace Hypa.Terminal.Vt.Osc;

/// <summary>
/// Maps an OSC number to its handlers and its string policy.
/// The registry is read-only after <see cref="Builder.Build"/>.
/// A new sequence needs one registration and one handler type.
/// </summary>
public sealed class OscHandlerRegistry
{
    private readonly Dictionary<int, Entry> _byNumber;
    private readonly IControlSequenceObserver[] _observers;

    private OscHandlerRegistry(Dictionary<int, Entry> byNumber, IControlSequenceObserver[] observers)
    {
        _byNumber = byNumber;
        _observers = observers;
    }

    internal IReadOnlyList<IControlSequenceObserver> Observers => _observers;

    internal bool TryFind(int number, out Entry entry) => _byNumber.TryGetValue(number, out entry);

    internal readonly record struct Entry(OscStringPolicy Policy, IOscHandler[] Handlers);

    public sealed class Builder
    {
        private readonly Dictionary<int, (OscStringPolicy Policy, List<IOscHandler> Handlers)> _byNumber = [];
        private readonly List<IControlSequenceObserver> _observers = [];

        /// <summary>
        /// Register a handler for an OSC number.
        /// Handlers for one number must share one policy, because the tokenizer reads the string once.
        /// </summary>
        public Builder Add(int number, OscStringPolicy policy, IOscHandler handler)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(number);
            ArgumentNullException.ThrowIfNull(handler);
            if (!_byNumber.TryGetValue(number, out var existing))
            {
                _byNumber[number] = (policy, [handler]);
            }
            else if (existing.Policy != policy)
            {
                throw new ArgumentException($"OSC {number} already has a different string policy.", nameof(policy));
            }
            else
            {
                existing.Handlers.Add(handler);
            }

            return this;
        }

        public Builder AddObserver(IControlSequenceObserver observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            _observers.Add(observer);
            return this;
        }

        public OscHandlerRegistry Build()
        {
            var entries = new Dictionary<int, Entry>(_byNumber.Count);
            foreach (var (number, value) in _byNumber)
                entries[number] = new Entry(value.Policy, [.. value.Handlers]);
            return new OscHandlerRegistry(entries, [.. _observers]);
        }
    }
}

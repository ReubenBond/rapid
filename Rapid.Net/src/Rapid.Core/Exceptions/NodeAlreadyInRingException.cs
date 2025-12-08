using Rapid.Pb;

namespace Rapid.Exceptions;

public sealed class NodeAlreadyInRingException : Exception
{
    public NodeAlreadyInRingException(Endpoint node) : this(node?.ToString() ?? throw new ArgumentNullException(nameof(node)))
    {
    }

    public NodeAlreadyInRingException()
    {
    }

    public NodeAlreadyInRingException(string message) : base(message)
    {
    }

    public NodeAlreadyInRingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}


namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// A simple scheduled item that wraps an Action callback.
/// Used for general-purpose delayed execution.
/// </summary>
internal sealed class ScheduledActionItem(Action callback) : ScheduledItem
{
    /// <inheritdoc />
    protected internal override void Invoke() => callback();
}

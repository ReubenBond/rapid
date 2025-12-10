using System.Diagnostics.CodeAnalysis;

namespace Rapid.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public class BroadcastEnumerableTests
{
    [Fact]
    public async Task MoveNextAsync_CompletesImmediately_WhenNextElementIsAlreadyAvailable()
    {
        // Arrange
        using var broadcast = new BroadcastEnumerable<int>();
        
        // Publish first element
        broadcast.TryPublish(1);
        
        // Get enumerator - should start at element 1
        var enumerator = broadcast.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        
        // Publish second element - this should complete the "next" TCS of element 1
        broadcast.TryPublish(2);
        
        // Act - call MoveNextAsync
        var task = enumerator.MoveNextAsync();
        
        // Assert - task should be completed synchronously
        Assert.True(task.IsCompleted, "MoveNextAsync should complete immediately when next element is available");
        Assert.True(await task, "MoveNextAsync should return true");
        Assert.Equal(2, enumerator.Current);
    }
    
    [Fact]
    public void MoveNextAsync_DoesNotCompleteImmediately_WhenNextElementIsNotAvailable()
    {
        // Arrange
        using var broadcast = new BroadcastEnumerable<int>();
        
        // Publish first element
        broadcast.TryPublish(1);
        
        // Get enumerator - should start at element 1
        var enumerator = broadcast.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        
        // Act - call MoveNextAsync WITHOUT publishing next element
        var task = enumerator.MoveNextAsync();
        
        // Assert - task should NOT be completed
        Assert.False(task.IsCompleted, "MoveNextAsync should not complete when next element is not available");
    }
}

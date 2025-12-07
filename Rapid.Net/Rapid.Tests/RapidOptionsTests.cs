using Google.Protobuf;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for RapidOptions configuration functionality.
/// </summary>
internal class RapidOptionsTests
{
    #region ListenAddress Tests

    [Fact]
    public void ListenAddressDefaultIsNull()
    {
        var options = new RapidOptions();
        Assert.Null(options.ListenAddress);
    }

    [Fact]
    public void ListenAddressCanBeSet()
    {
        var options = new RapidOptions
        {
            ListenAddress = Utils.HostFromParts("127.0.0.1", 1234)
        };

        Assert.NotNull(options.ListenAddress);
        Assert.Equal("127.0.0.1", options.ListenAddress.Hostname.ToStringUtf8());
        Assert.Equal(1234, options.ListenAddress.Port);
    }

    #endregion

    #region SeedAddress Tests

    [Fact]
    public void SeedAddressDefaultIsNull()
    {
        var options = new RapidOptions();
        Assert.Null(options.SeedAddress);
    }

    [Fact]
    public void SeedAddressCanBeSet()
    {
        var options = new RapidOptions
        {
            SeedAddress = Utils.HostFromParts("192.168.1.1", 9000)
        };

        Assert.NotNull(options.SeedAddress);
        Assert.Equal("192.168.1.1", options.SeedAddress.Hostname.ToStringUtf8());
        Assert.Equal(9000, options.SeedAddress.Port);
    }

    [Fact]
    public void SeedAddressCanBeSameAsListenAddressForSeedNode()
    {
        var address = Utils.HostFromParts("127.0.0.1", 1234);
        var options = new RapidOptions
        {
            ListenAddress = address,
            SeedAddress = address
        };

        Assert.Equal(options.ListenAddress, options.SeedAddress);
    }

    #endregion

    #region Metadata Tests

    [Fact]
    public void MetadataDefaultIsEmptyMetadata()
    {
        var options = new RapidOptions();

        Assert.NotNull(options.Metadata);
        Assert.Empty(options.Metadata.Metadata_);
    }

    [Fact]
    public void MetadataCanBeSetDirectly()
    {
        var metadata = new Metadata();
        metadata.Metadata_.Add("key", ByteString.CopyFromUtf8("value"));

        var options = new RapidOptions { Metadata = metadata };

        Assert.Single(options.Metadata.Metadata_);
        Assert.Equal("value", options.Metadata.Metadata_["key"].ToStringUtf8());
    }

    #endregion

    #region SetMetadata Tests

    [Fact]
    public void SetMetadataSetsMetadataFromDictionary()
    {
        var options = new RapidOptions();
        var dict = new Dictionary<string, ByteString>
        {
            ["role"] = ByteString.CopyFromUtf8("leader"),
            ["datacenter"] = ByteString.CopyFromUtf8("us-west")
        };

        options.SetMetadata(dict);

        Assert.Equal(2, options.Metadata.Metadata_.Count);
        Assert.Equal("leader", options.Metadata.Metadata_["role"].ToStringUtf8());
        Assert.Equal("us-west", options.Metadata.Metadata_["datacenter"].ToStringUtf8());
    }

    [Fact]
    public void SetMetadataEmptyDictionaryClearsMetadata()
    {
        var options = new RapidOptions();
        options.SetMetadata(new Dictionary<string, ByteString>
        {
            ["initial"] = ByteString.CopyFromUtf8("value")
        });

        options.SetMetadata(new Dictionary<string, ByteString>());

        Assert.Empty(options.Metadata.Metadata_);
    }

    [Fact]
    public void SetMetadataOverwritesPreviousMetadata()
    {
        var options = new RapidOptions();
        options.SetMetadata(new Dictionary<string, ByteString>
        {
            ["old"] = ByteString.CopyFromUtf8("value")
        });

        options.SetMetadata(new Dictionary<string, ByteString>
        {
            ["new"] = ByteString.CopyFromUtf8("value")
        });

        Assert.Single(options.Metadata.Metadata_);
        Assert.False(options.Metadata.Metadata_.ContainsKey("old"));
        Assert.True(options.Metadata.Metadata_.ContainsKey("new"));
    }

    [Fact]
    public void SetMetadataNullDictionaryThrowsArgumentNullException()
    {
        var options = new RapidOptions();

        Assert.Throws<ArgumentNullException>(() => options.SetMetadata(null!));
    }

    [Fact]
    public void SetMetadataMultipleEntriesAllStored()
    {
        var options = new RapidOptions();
        var dict = new Dictionary<string, ByteString>
        {
            ["a"] = ByteString.CopyFromUtf8("1"),
            ["b"] = ByteString.CopyFromUtf8("2"),
            ["c"] = ByteString.CopyFromUtf8("3"),
            ["d"] = ByteString.CopyFromUtf8("4"),
            ["e"] = ByteString.CopyFromUtf8("5")
        };

        options.SetMetadata(dict);

        Assert.Equal(5, options.Metadata.Metadata_.Count);
    }

    [Fact]
    public void SetMetadataBinaryDataStoredCorrectly()
    {
        var options = new RapidOptions();
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var dict = new Dictionary<string, ByteString>
        {
            ["binary"] = ByteString.CopyFrom(binaryData)
        };

        options.SetMetadata(dict);

        var stored = options.Metadata.Metadata_["binary"].ToByteArray();
        Assert.Equal(binaryData, stored);
    }

    #endregion

    #region Subscriptions Tests

    [Fact]
    public void SubscriptionsDefaultIsEmpty()
    {
        var options = new RapidOptions();
        Assert.Empty(options.Subscriptions);
    }

    [Fact]
    public void AddSubscriptionViewChangeAddsSubscription()
    {
        var options = new RapidOptions();
        Action<ClusterStatusChange> callback = _ => { };

        options.AddSubscription(ClusterEvents.ViewChange, callback);

        Assert.Single(options.Subscriptions);
        Assert.True(options.Subscriptions.ContainsKey(ClusterEvents.ViewChange));
        Assert.Single(options.Subscriptions[ClusterEvents.ViewChange]);
    }

    [Fact]
    public void AddSubscriptionViewChangeProposalAddsSubscription()
    {
        var options = new RapidOptions();
        Action<ClusterStatusChange> callback = _ => { };

        options.AddSubscription(ClusterEvents.ViewChangeProposal, callback);

        Assert.True(options.Subscriptions.ContainsKey(ClusterEvents.ViewChangeProposal));
    }

    [Fact]
    public void AddSubscriptionMultipleCallbacksSameEventAllAdded()
    {
        var options = new RapidOptions();
        Action<ClusterStatusChange> callback1 = _ => { };
        Action<ClusterStatusChange> callback2 = _ => { };
        Action<ClusterStatusChange> callback3 = _ => { };

        options.AddSubscription(ClusterEvents.ViewChange, callback1);
        options.AddSubscription(ClusterEvents.ViewChange, callback2);
        options.AddSubscription(ClusterEvents.ViewChange, callback3);

        Assert.Equal(3, options.Subscriptions[ClusterEvents.ViewChange].Count);
    }

    [Fact]
    public void AddSubscriptionDifferentEventsAllAdded()
    {
        var options = new RapidOptions();
        Action<ClusterStatusChange> callback1 = _ => { };
        Action<ClusterStatusChange> callback2 = _ => { };

        options.AddSubscription(ClusterEvents.ViewChange, callback1);
        options.AddSubscription(ClusterEvents.ViewChangeProposal, callback2);

        Assert.Equal(2, options.Subscriptions.Count);
        Assert.True(options.Subscriptions.ContainsKey(ClusterEvents.ViewChange));
        Assert.True(options.Subscriptions.ContainsKey(ClusterEvents.ViewChangeProposal));
    }

    [Fact]
    public void AddSubscriptionCallbackIsInvokable()
    {
        var options = new RapidOptions();
        var invokeCount = 0;
        Action<ClusterStatusChange> callback = _ => invokeCount++;

        options.AddSubscription(ClusterEvents.ViewChange, callback);

        var change = new ClusterStatusChange(1, [], []);
        options.Subscriptions[ClusterEvents.ViewChange][0](change);

        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public void AddSubscriptionSameCallbackMultipleTimesAllAdded()
    {
        var options = new RapidOptions();
        Action<ClusterStatusChange> callback = _ => { };

        options.AddSubscription(ClusterEvents.ViewChange, callback);
        options.AddSubscription(ClusterEvents.ViewChange, callback);

        Assert.Equal(2, options.Subscriptions[ClusterEvents.ViewChange].Count);
    }

    #endregion

    #region Full Configuration Tests

    [Fact]
    public void FullConfigurationAllPropertiesSet()
    {
        var listenAddr = Utils.HostFromParts("10.0.0.1", 5000);
        var seedAddr = Utils.HostFromParts("10.0.0.2", 5000);
        var changeCount = 0;

        var options = new RapidOptions
        {
            ListenAddress = listenAddr,
            SeedAddress = seedAddr
        };

        options.SetMetadata(new Dictionary<string, ByteString>
        {
            ["role"] = ByteString.CopyFromUtf8("worker"),
            ["version"] = ByteString.CopyFromUtf8("1.0.0")
        });

        options.AddSubscription(ClusterEvents.ViewChange, _ => changeCount++);

        Assert.Equal(listenAddr, options.ListenAddress);
        Assert.Equal(seedAddr, options.SeedAddress);
        Assert.Equal(2, options.Metadata.Metadata_.Count);
        Assert.Single(options.Subscriptions);
    }

    #endregion
}

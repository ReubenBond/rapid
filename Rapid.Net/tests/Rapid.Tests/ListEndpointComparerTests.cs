using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for ListEndpointComparer equality and hashing functionality.
/// </summary>
public class ListEndpointComparerTests
{
    private static readonly ListEndpointComparer Comparer = ListEndpointComparer.Instance;


    [Fact]
    public void EqualsBothNullReturnsTrue() => Assert.True(Comparer.Equals(null, null));

    [Fact]
    public void EqualsFirstNullReturnsFalse()
    {
        var list = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        Assert.False(Comparer.Equals(null, list));
    }

    [Fact]
    public void EqualsSecondNullReturnsFalse()
    {
        var list = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        Assert.False(Comparer.Equals(list, null));
    }

    [Fact]
    public void EqualsBothEmptyReturnsTrue() => Assert.True(Comparer.Equals([], []));

    [Fact]
    public void EqualsSameElementsReturnsTrue()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };

        Assert.True(Comparer.Equals(list1, list2));
    }

    [Fact]
    public void EqualsSameElementsDifferentOrderReturnsFalse()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.2", 1235),
            Utils.HostFromParts("127.0.0.1", 1234)
        };

        Assert.False(Comparer.Equals(list1, list2));
    }

    [Fact]
    public void EqualsDifferentLengthsReturnsFalse()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };

        Assert.False(Comparer.Equals(list1, list2));
    }

    [Fact]
    public void EqualsDifferentElementsReturnsFalse()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 9999)
        };

        Assert.False(Comparer.Equals(list1, list2));
    }

    [Fact]
    public void EqualsSameReferenceReturnsTrue()
    {
        var list = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        Assert.True(Comparer.Equals(list, list));
    }

    [Fact]
    public void EqualsManyElementsReturnsTrue()
    {
        var list1 = new List<Endpoint>();
        var list2 = new List<Endpoint>();

        for (var i = 0; i < 100; i++)
        {
            list1.Add(Utils.HostFromParts("192.168.1." + i, 5000 + i));
            list2.Add(Utils.HostFromParts("192.168.1." + i, 5000 + i));
        }

        Assert.True(Comparer.Equals(list1, list2));
    }



    [Fact]
    public void GetHashCodeEqualListsReturnsSameHashCode()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };

        Assert.Equal(Comparer.GetHashCode(list1), Comparer.GetHashCode(list2));
    }

    [Fact]
    public void GetHashCodeEmptyListsReturnsSameHashCode() => Assert.Equal(Comparer.GetHashCode([]), Comparer.GetHashCode([]));

    [Fact]
    public void GetHashCodeDifferentListsLikelyDifferentHashCodes()
    {
        var list1 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        var list2 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1235) };

        Assert.NotEqual(Comparer.GetHashCode(list1), Comparer.GetHashCode(list2));
    }

    [Fact]
    public void GetHashCodeOrderMatters()
    {
        var list1 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.1", 1234),
            Utils.HostFromParts("127.0.0.2", 1235)
        };
        var list2 = new List<Endpoint>
        {
            Utils.HostFromParts("127.0.0.2", 1235),
            Utils.HostFromParts("127.0.0.1", 1234)
        };

        // Different order means different sequences - they should not be equal
        Assert.False(Comparer.Equals(list1, list2));
    }



    [Fact]
    public void DictionaryCanUseListAsKey()
    {
        var dict = new Dictionary<List<Endpoint>, int>(Comparer);

        var key1 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        dict[key1] = 100;

        var key2 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        Assert.True(dict.TryGetValue(key2, out var value));
        Assert.Equal(100, value);
    }

    [Fact]
    public void DictionaryDifferentKeysStored()
    {
        var dict = new Dictionary<List<Endpoint>, int>(Comparer);

        var key1 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        var key2 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1235) };

        dict[key1] = 100;
        dict[key2] = 200;

        Assert.Equal(2, dict.Count);
        Assert.Equal(100, dict[key1]);
        Assert.Equal(200, dict[key2]);
    }

    [Fact]
    public void DictionaryUpdateExistingKey()
    {
        var dict = new Dictionary<List<Endpoint>, int>(Comparer);

        var key1 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        dict[key1] = 100;

        var key2 = new List<Endpoint> { Utils.HostFromParts("127.0.0.1", 1234) };
        dict[key2] = 200;

        Assert.Single(dict);
        Assert.Equal(200, dict[key1]);
    }



    [Fact]
    public void InstanceReturnsSameInstance()
    {
        var instance1 = ListEndpointComparer.Instance;
        var instance2 = ListEndpointComparer.Instance;

        Assert.Same(instance1, instance2);
    }

}

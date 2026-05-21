using System.Runtime.CompilerServices;
using YFex.State.Notification;

namespace YFex.State.Tests.Notifications;

public class ChangedNotificationTests
{
    [Fact]
    public void Construction_RequiredProperties_AreCarriedThrough()
    {
        var n = new ChangedNotification { PropertyName = "X", PropertyId = 7u };

        n.PropertyName.Should().Be("X");
        n.PropertyId.Should().Be(7u);
        n.Kind.Should().Be(ChangeKind.PropertyChanged);
        n.Index.Should().Be(0);
        n.Count.Should().Be(0);
    }

    [Fact]
    public void With_Expression_PreservesImmutabilityAndUpdatesOnlySpecifiedFields()
    {
        var original = new ChangedNotification { PropertyName = "Items", PropertyId = 0u };
        var withIndex = original with { Kind = ChangeKind.ItemsAdded, Index = 5, Count = 3 };

        original.Kind.Should().Be(ChangeKind.PropertyChanged);
        original.Index.Should().Be(0);
        original.Count.Should().Be(0);

        withIndex.PropertyName.Should().Be("Items");
        withIndex.PropertyId.Should().Be(0u);
        withIndex.Kind.Should().Be(ChangeKind.ItemsAdded);
        withIndex.Index.Should().Be(5);
        withIndex.Count.Should().Be(3);
    }

    [Fact]
    public void Struct_PassedByIn_DoesNotBox()
    {
        // Just verifying the type is a value type — boxing checks live in allocation tests.
        typeof(ChangedNotification).IsValueType.Should().BeTrue();
    }

    [Fact]
    public void Size_IsBounded_ToAvoidAccidentalGrowth()
    {
        // Sanity check: stays within reasonable bounds for `in` passing.
        // Layout: string ref (8) + object ref OldItem (8) + uint (4) + ChangeKind (1) + int (4) + int (4)
        //   = 29 + padding/alignment. Allow up to 40 bytes.
        Unsafe.SizeOf<ChangedNotification>().Should().BeLessOrEqualTo(40);
    }

    [Theory]
    [InlineData(ChangeKind.PropertyChanged, 0)]
    [InlineData(ChangeKind.ItemsAdded, 1)]
    [InlineData(ChangeKind.ItemsRemoved, 2)]
    [InlineData(ChangeKind.ItemReplaced, 3)]
    [InlineData(ChangeKind.ItemsCleared, 4)]
    [InlineData(ChangeKind.ItemsReset, 5)]
    public void ChangeKind_Values_AreStable(ChangeKind kind, int expectedNumeric)
    {
        ((byte)kind).Should().Be((byte)expectedNumeric);
    }

    [Fact]
    public void ChangeKind_DefaultIsPropertyChanged()
    {
        default(ChangeKind).Should().Be(ChangeKind.PropertyChanged);
    }

    [Fact]
    public void OldItem_DefaultsToNull()
    {
        var n = new ChangedNotification { PropertyName = "X", PropertyId = 0u };
        n.OldItem.Should().BeNull();
    }

    [Fact]
    public void OldItem_SetViaInit_AccessibleInHandler()
    {
        var n = new ChangedNotification
        {
            PropertyName = "Items",
            PropertyId = 0u,
            Kind = ChangeKind.ItemReplaced,
            Index = 2,
            Count = 1,
            OldItem = "previous"
        };

        n.OldItem.Should().Be("previous");
    }

    [Fact]
    public void OldItem_PropagatesThroughWithExpression()
    {
        var template = new ChangedNotification
        {
            PropertyName = "Items",
            PropertyId = 0u,
            Kind = ChangeKind.ItemReplaced
        };

        var customised = template with { Index = 5, OldItem = 42 };

        customised.OldItem.Should().Be(42);
        template.OldItem.Should().BeNull("with expression preserves immutability of original");
    }
}

using Luthn.Core.Common;
using Luthn.Core.Graph;

namespace Luthn.Core.Tests;

public sealed class CoreGraphModelTests
{
    [Fact]
    public void PublicRecordIdRejectsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new PublicRecordId("bad id"));
    }

    [Fact]
    public void RelationshipRejectsSelfReference()
    {
        var id = new PublicRecordId("entity-1");

        Assert.Throws<ArgumentException>(() =>
            new CoreRelationship(
                new PublicRecordId("relationship-1"),
                CoreRelationshipType.References,
                id,
                id));
    }
}

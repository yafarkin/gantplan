using GantPlan.Dtos.Enums;

namespace Tests;

public sealed class ExtensionTests
{
    [Test]
    public void ToDaysTest()
    {
        const TShirtType t = TShirtType.L;
        
        var result = t.ToDays(100);
        Assert.That(result, Is.EqualTo(8));
        
        result = t.ToDays(75);
        Assert.That(result, Is.EqualTo(10));

        result = t.ToDays(50);
        Assert.That(result, Is.EqualTo(12));
        
        result = t.ToDays(25);
        Assert.That(result, Is.EqualTo(13));

        result = t.ToDays(0);
        Assert.That(result, Is.EqualTo(15));
        
    }
}
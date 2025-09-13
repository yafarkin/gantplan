using GantPlan.Dtos;
using GantPlan.Logic;

namespace Tests;

public sealed class SmokeTests
{
    private ProjectDto _project;
    private Solver _solver;
    
    [SetUp]
    public void Setup()
    {
        _solver = new Solver();
    }

    [Test]
    public void SmokeTest()
    {
        Assert.Pass();
        // _project = new ProjectDto
        // {
        //     ProjectStart = 
        // }
    }
}
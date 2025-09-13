using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace Planner.SourcePlans;

public static class DemoProject
{
    public static ProjectDto Build()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            FactDate = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "Demo project",
                Children =
                [
                    new TaskDto
                    {
                        Id = "1.1",
                        Name = "Business feature 1",
                        WorkType = WorkType.Business,
                        Children = [
                            new TaskDto
                            {
                                Id = "1.1.1.10",
                                Name = "BF-1. Backend development",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-be",
                                    TShirt = TShirtType.L
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.1.2.10",
                                Name = "BF-1. Frontend development",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-fe",
                                    TShirt = TShirtType.M
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.1.3.10",
                                Name = "BF-1. Testing",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "qa",
                                    TShirt = TShirtType.M,
                                    PredecessorIds = ["1.1.1.10", "1.1.2.10"]
                                }
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "1.2",
                        Name = "Business feature 2",
                        WorkType = WorkType.Business,
                        Children = [
                            new TaskDto
                            {
                                Id = "1.2.1.10",
                                Name = "BF-2. Backend development",
                                Limit = new TaskLimitDto
                                {
                                    Priority = 1,
                                    ResourceRole = "dev-be",
                                    TShirt = TShirtType.M
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.2.3.10",
                                Name = "BF-2. Testing",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "qa",
                                    TShirt = TShirtType.M,
                                    PredecessorIds = ["1.2.1.10"]
                                }
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "1.3",
                        Name = "Technical feature 1",
                        WorkType = WorkType.Team,
                        Children = [
                            new TaskDto
                            {
                                Id = "1.3.2.10",
                                Name = "TF-1. Frontend development",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-fe",
                                    TShirt = TShirtType.S
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.3.3.10",
                                Name = "TF-1. Testing",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "qa",
                                    TShirt = TShirtType.M,
                                    PredecessorIds = ["1.3.2.10"]
                                }
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "1.4",
                        Name = "Technical feature 2",
                        WorkType = WorkType.Team,
                        Children = [
                            new TaskDto
                            {
                                Id = "1.4.1.10",
                                Name = "TF-2. Backend development",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-be",
                                    TShirt = TShirtType.M
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.4.2.10",
                                Name = "TF-2. Frontend development",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-fe",
                                    TShirt = TShirtType.S
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.4.3.10",
                                Name = "TF-2. Testing",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "qa",
                                    TShirt = TShirtType.S,
                                    PredecessorIds = ["1.4.1.10", "1.4.2.10"]
                                }
                            }
                        ]
                    }
                ]
            },
            GlobalCalendar = new CalendarDto
            {
                NonWorkingDays = new List<CalendarPeriod>
                {
                    new CalendarPeriod
                    {
                        From = new DateOnly(2026, 1, 1),
                        To = new DateOnly(2026, 1, 11)
                    }
                }
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Name = "developer 1",
                    Role = "dev-be"
                },
                new()
                {
                    Name = "developer 2",
                    Role = "dev-be",
                    AvailFrom = new DateOnly(2026, 1, 19),
                },
                new()
                {
                    Name = "developer 3",
                    Role = "dev-fe",
                    Calendar = new ()
                    {
                        NonWorkingDays = new  List<CalendarPeriod>
                        {
                            new ()
                            {
                                From = new DateOnly(2026, 1, 20),
                                To = new DateOnly(2026, 1, 21)
                            }
                        }
                    }
                },
                new()
                {
                    Name = "qa 1",
                    Role = "qa"
                },
            }
        };

        return project;
    }
}
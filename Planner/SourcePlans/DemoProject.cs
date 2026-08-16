using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace Planner.SourcePlans;

public static class DemoProject
{
    public static ProjectDto Build()
    {
        var project = new ProjectDto
        {
            // Старт проекта = старт самой ранней (уже выполненной) задачи
            // DEMO-050 - иначе на диаграмме перед DEMO-050 висел бы пустой
            // отрезок времени без единой задачи.
            ProjectStart = new DateOnly(2025, 11, 24),
            // Дата среза "на сегодня" - без неё Solver.PostFillAfterSolve не
            // сможет сам разметить факт (см. FactDateShouldAutoFillCompletedAndInProgressTest),
            // а Solver.CreateDependencyBetweenTasks не включит защиту от
            // бэкдейтинга новых задач в дыру, оставленную уже выполненной
            // DEMO-050 (см. hasAlreadyCompletedWork) - без FactDate это не
            // синтетический пример, а баг во входных данных. Дата - не
            // раньше самой поздней Fact-отметки в DEMO-075 (иначе
            // получилось бы "сегодня раньше, чем последняя отметка о
            // прогрессе", что бессмысленно).
            FactDate = new DateOnly(2025, 12, 3),
            BaseJiraUrl = "https://example.atlassian.net/browse/",
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "Demo project",
                // Обязательный тег (см. RequiredTaskTags) - выставлен один раз
                // на корне и наследуется вниз всем задачам, у которых свой не задан.
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children =
                [
                    // DEMO-050 - уже полностью выполненная задача (Fact
                    // содержит Completed по каждому листу), даты целиком в
                    // прошлом (до 2026 года). У Solver'а для неё нет ни
                    // одной переменной start/end (TaskDto.CanSkipTask == true
                    // из-за Fact.IsFinished), поэтому она в принципе не может
                    // измениться при пересчёте - Plan/Fact ниже выставлены
                    // руками, как если бы их когда-то записал предыдущий
                    // прогон солвера + отметки о факте выполнения.
                    new TaskDto
                    {
                        Id = "1.0",
                        Name = "Business feature 0",
                        JiraKey = "DEMO-050",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Business.ToString() },
                        Children = [
                            new TaskDto
                            {
                                Id = "1.0.1.10",
                                Name = "BF-0. Backend development",
                                JiraKey = "DEMO-051",
                                Limit = new TaskLimitDto { ResourceRole = "dev-be", Duration = 5 },
                                Plan = new TaskPlanDto
                                {
                                    ResourceName = "developer 1",
                                    PlannedStart = new DateOnly(2025, 11, 24),
                                    PlannedFinish = new DateOnly(2025, 11, 28)
                                },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 24),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "developer 1",
                                            Duration = 5
                                        },
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 28),
                                            Type = TaskFactRecordType.Completed,
                                            ResourceName = "developer 1"
                                        }
                                    ]
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.0.2.10",
                                Name = "BF-0. Frontend development",
                                JiraKey = "DEMO-052",
                                Limit = new TaskLimitDto { ResourceRole = "dev-fe", Duration = 3 },
                                Plan = new TaskPlanDto
                                {
                                    ResourceName = "developer 3",
                                    PlannedStart = new DateOnly(2025, 11, 24),
                                    PlannedFinish = new DateOnly(2025, 11, 26)
                                },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 24),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "developer 3",
                                            Duration = 3
                                        },
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 26),
                                            Type = TaskFactRecordType.Completed,
                                            ResourceName = "developer 3"
                                        }
                                    ]
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.0.3.10",
                                Name = "BF-0. Testing",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "qa",
                                    Duration = 2,
                                    PredecessorIds = ["1.0.1.10", "1.0.2.10"]
                                },
                                Plan = new TaskPlanDto
                                {
                                    ResourceName = "qa 1",
                                    PlannedStart = new DateOnly(2025, 12, 1),
                                    PlannedFinish = new DateOnly(2025, 12, 2)
                                },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 12, 1),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "qa 1",
                                            Duration = 2
                                        },
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 12, 2),
                                            Type = TaskFactRecordType.Completed,
                                            ResourceName = "qa 1"
                                        }
                                    ]
                                }
                            }
                        ]
                    },
                    // DEMO-075 - начата ещё в 2025-м, сразу вслед за DEMO-050
                    // (теми же людьми - dev-be/dev-fe/qa у нас всего по одному
                    // человеку до начала 2026-го, так что раньше своего
                    // освобождения от DEMO-050 они физически не могли на неё
                    // переключиться), до сих пор в работе (Fact.IsProgress ==
                    // true) и без приоритета. У каждой задачи - роль в Limit
                    // ("кто-то из dev/qa"), а конкретный человек - только в
                    // единственной Fact-записи (ResourceName) - так это и
                    // работает по-настоящему: солвер запрещает менять
                    // исполнителя уже начатой задаче (см.
                    // Solver.FindResourcesForTask) и жёстко фиксирует старт
                    // остатка датой этой записи (см. Solver.CreateTasksIntervals).
                    // Дата записи стоит практически сразу после освобождения
                    // от DEMO-050 - если оставить между ними большой зазор,
                    // солвер вставит туда DEMO-100 (см. ниже), а не заставит
                    // её ждать; а остаток (Duration) выбран заметно больше,
                    // чем расстояние от даты записи до конца декабря - иначе
                    // он бы весь уместился в декабре и не "перетёк" в январь.
                    new TaskDto
                    {
                        Id = "1.05",
                        Name = "Business feature 0.5",
                        JiraKey = "DEMO-075",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Business.ToString() },
                        Children = [
                            // сделано примерно половина (60 дней оценка, 30 осталось).
                            // Started идёт первой записью, до InProgress-чекпоинта -
                            // без неё Solver.PostFillAfterSolve раньше считал, что
                            // задачу "ещё не трогали", и молча дописывал вторую,
                            // пересчитанную с нуля из Limit.Duration оценку остатка
                            // (баг, найденный на реальном двойном прогоне Planner -
                            // см. Solver.cs, hasAnyProgressRecord).
                            new TaskDto
                            {
                                Id = "1.05.1.10",
                                Name = "BF-0.5. Backend development",
                                JiraKey = "DEMO-076",
                                Limit = new TaskLimitDto { ResourceRole = "dev-be", Duration = 60 },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 24),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "developer 1",
                                            Duration = 60
                                        },
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 29),
                                            Type = TaskFactRecordType.InProgress,
                                            ResourceName = "developer 1",
                                            Duration = 30
                                        }
                                    ]
                                }
                            },
                            // сделано примерно треть (42 дня оценка, 28 осталось)
                            new TaskDto
                            {
                                Id = "1.05.2.10",
                                Name = "BF-0.5. Frontend development",
                                JiraKey = "DEMO-077",
                                Limit = new TaskLimitDto { ResourceRole = "dev-fe", Duration = 42 },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 24),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "developer 3",
                                            Duration = 42
                                        },
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 11, 27),
                                            Type = TaskFactRecordType.InProgress,
                                            ResourceName = "developer 3",
                                            Duration = 28
                                        }
                                    ]
                                }
                            },
                            // только-только начата - вся оценка ещё впереди
                            new TaskDto
                            {
                                Id = "1.05.3.10",
                                Name = "BF-0.5. Testing",
                                Limit = new TaskLimitDto { ResourceRole = "qa", Duration = 25 },
                                Fact = new TaskFactDto
                                {
                                    Records =
                                    [
                                        new TaskFactRecordDto
                                        {
                                            RecordedAt = new DateOnly(2025, 12, 3),
                                            Type = TaskFactRecordType.Started,
                                            ResourceName = "qa 1",
                                            Duration = 25
                                        }
                                    ]
                                }
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "1.1",
                        Name = "Business feature 1",
                        JiraKey = "DEMO-100",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Business.ToString() },
                        // Priority только затем, чтобы вниз по дереву
                        // унаследовался всем трём задачам ниже (см.
                        // TaskAttributeInheritance) - у самого контейнера
                        // start/end в модели солвера нет, он никогда не
                        // решается напрямую.
                        Limit = new TaskLimitDto { Priority = 1 },
                        Children = [
                            new TaskDto
                            {
                                Id = "1.1.1.10",
                                Name = "BF-1. Backend development",
                                JiraKey = "DEMO-101",
                                Limit = new TaskLimitDto
                                {
                                    // тот же человек, что уже занят на
                                    // DEMO-075 - несмотря на приоритет 1,
                                    // задача не сможет стартовать раньше, чем
                                    // он освободится (см. AddNoOverlap).
                                    // Если оставить только роль, солвер мог
                                    // бы взять свободного "developer 2" и
                                    // запустить эту задачу сразу же.
                                    ResourceRole = "dev-be",
                                    ResourceName = "developer 1",
                                    TShirt = TShirtType.L
                                }
                            },
                            new TaskDto
                            {
                                Id = "1.1.2.10",
                                Name = "BF-1. Frontend development",
                                JiraKey = "DEMO-102",
                                Limit = new TaskLimitDto
                                {
                                    ResourceRole = "dev-fe",
                                    ResourceName = "developer 3",
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
                                    ResourceName = "qa 1",
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
                        JiraKey = "DEMO-200",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Business.ToString() },
                        Children = [
                            new TaskDto
                            {
                                Id = "1.2.1.10",
                                Name = "BF-2. Backend development",
                                JiraKey = "DEMO-201",
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
                        JiraKey =  "DEMO-300",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Team.ToString() },
                        Children = [
                            new TaskDto
                            {
                                Id = "1.3.2.10",
                                Name = "TF-1. Frontend development",
                                JiraKey = "DEMO-301",
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
                        JiraKey = "DEMO-400",
                        Tags = new() { [TaskTagKeys.WorkType] = WorkType.Team.ToString() },
                        Children = [
                            new TaskDto
                            {
                                Id = "1.4.1.10",
                                Name = "TF-2. Backend development",
                                JiraKey = "DEMO-401",
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
                                JiraKey = "DEMO-402",
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
                // Занятость developer 1/3 и qa 1 на DEMO-050 отдельным
                // AvailFrom больше не проставляем - её теперь закрывает
                // общий флор в Solver.CreateDependencyBetweenTasks (свежая
                // задача не может стартовать раньше FactDate, если в проекте
                // уже есть что-то Completed - см. hasAlreadyCompletedWork).
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
namespace GantPlan.Dtos;

/// <summary>
/// Ключи <see cref="TaskDto.Tags"/>, которые обязаны быть заполнены (после
/// наследования от родителя) у каждой листовой задачи - иначе
/// <see cref="GantPlan.Logic.ProjectValidator"/> сочтёт это ошибкой
/// пред-валидации и не даст дойти до Solve(). Чтобы сделать новый тег
/// обязательным, достаточно добавить его сюда - ProjectValidator трогать
/// не нужно.
/// </summary>
public static class RequiredTaskTags
{
    public static readonly IReadOnlyCollection<string> Keys =
    [
        TaskTagKeys.IsOkr
    ];
}

namespace GantPlan.Dtos;

/// <summary>
/// Константы для уже используемых ключей <see cref="TaskDto.Tags"/> - просто
/// чтобы не разводить опечатки в magic strings по коду. Список не является
/// исчерпывающим: TaskDto.Tags принимает любые ключи, это не enum.
/// </summary>
public static class TaskTagKeys
{
    /// <summary>Инициатор работы - см. <see cref="Enums.WorkType"/> для типовых значений.</summary>
    public const string WorkType = "WorkType";

    /// <summary>Признак того, что задача относится к OKR ("true"/"false").</summary>
    public const string IsOkr = "IsOkr";
}

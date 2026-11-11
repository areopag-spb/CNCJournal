namespace AiService.Models;

/// <summary>
/// Запрос фоновой проверки ОДНОЙ записи сутко-станка: релевантно ли комментарии
/// мастера объясняют аномалии строки. Список аномалий вычисляет и присылает
/// КЛИЕНТ — авторитетные условия живут в remeLog Part.this[columnName]
/// (SetupTimePlanForCalc/HasOrder и пр. на сервере недоступны); сервер аномалии
/// не выводит, только рендерит их в промпт.
/// </summary>
public class VerifyPartRequest
{
    public string Machine { get; set; } = "";
    public string ShiftDate { get; set; } = "";
    public PartContext Part { get; set; } = new();
    public List<VerifyAnomaly> Anomalies { get; set; } = [];
    public string? Model { get; set; }
}

public class VerifyAnomaly
{
    /// <summary> Имя поля комментария, объясняющего аномалию (MasterSetupComment и т.д.). </summary>
    public string Field { get; set; } = "";

    /// <summary> Человекочитаемое описание аномалии с числами («КПД наладки 45% &lt; 70%»). </summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// Ответ проверки. Совещательная семантика: любая ошибка (транспорт, парсинг,
/// таймаут) трактуется клиентом как Ok=true + Error — ошибка не должна выглядеть
/// замечанием и ничего не блокирует.
/// </summary>
public class VerifyPartResponse
{
    public bool Ok { get; set; }
    public string Remark { get; set; } = "";
    public string? Error { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>
    /// Причина из справочника, на которую стоит сменить выбранную (замечание «неверно выбрана
    /// причина»): клиент предлагает замену одним действием. null — предложения нет.
    /// </summary>
    public string? SuggestedReason { get; set; }

    /// <summary> Поле причины, к которому относится SuggestedReason (MasterSetupComment / MasterMachiningComment). </summary>
    public string? SuggestedField { get; set; }
}

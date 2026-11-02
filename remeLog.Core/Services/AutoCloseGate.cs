using remeLog.Infrastructure;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace remeLog.Core.Services
{
    /// <summary> Что делать с сутками при автозакрытии. </summary>
    public enum AutoCloseOutcome
    {
        /// <summary> Закрыть без человека (применить <see cref="AutoCloseDecision.Exclusions"/>). </summary>
        Close,
        /// <summary> Решать рано: нет отчёта мастера или сутки не созрели. Остаются кандидатами на следующий запуск. </summary>
        Defer,
        /// <summary> Оставить человеку; причины — в <see cref="AutoCloseDecision.Reasons"/>. </summary>
        Manual,
    }

    public sealed class AutoCloseOptions
    {
        /// <summary> Минимальная уверенность ИИ для закрытия. </summary>
        public double MinConfidence { get; init; } = 0.8;

        /// <summary> Через сколько часов после конца суток (ShiftDate + 1 день) их можно закрывать. </summary>
        public int MaturityHours { get; init; } = 12;

        /// <summary>
        /// Допущенные версии промпта/правил. null — проверка выключена; непустой набор —
        /// сутки, проанализированные другой версией, остаются человеку.
        /// </summary>
        public IReadOnlyCollection<string>? AllowedPromptVersions { get; init; }
    }

    public sealed record AutoCloseRequest(
        AiAnalysisResult Result,
        IReadOnlyList<Part> Parts,
        bool HasDayReport,
        bool HasNightReport,
        DateTime ShiftDate,
        DateTime Now,
        bool AlreadyReviewedByHuman,
        bool AlreadyChecked);

    public sealed record AutoCloseDecision(
        AutoCloseOutcome Outcome,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<(Part Part, string Reason)> Exclusions)
    {
        public bool CanClose => Outcome == AutoCloseOutcome.Close;
    }

    /// <summary>
    /// Единая точка решения «можно ли закрыть сутки без человека» (этап 2.3). Серверный
    /// <c>AutoCloseEligible</c> — необходимое, но не достаточное условие: здесь же
    /// проверяются данные клиента (отчёты мастера, созревание, сопоставимость строк
    /// для автоисключения). Любое сомнение — сутки человеку; нет отчёта или рано —
    /// откладываем. План: research/ai-service/auto-close-plan.md, §2.
    /// </summary>
    public static class AutoCloseGate
    {
        public static AutoCloseDecision Evaluate(AutoCloseRequest req, AutoCloseOptions options)
        {
            var none = Array.Empty<(Part, string)>();

            if (req.AlreadyChecked || req.AlreadyReviewedByHuman)
                return new(AutoCloseOutcome.Manual, new[] { "сутки уже проверены человеком" }, none);

            var defer = new List<string>();
            if (!req.HasDayReport) defer.Add("нет отчёта мастера за день");
            if (!req.HasNightReport) defer.Add("нет отчёта мастера за ночь");
            var matureAt = req.ShiftDate.Date.AddDays(1).AddHours(options.MaturityHours);
            if (req.Now < matureAt) defer.Add($"сутки ещё не созрели (до {matureAt:dd.MM HH:mm})");
            if (defer.Count > 0) return new(AutoCloseOutcome.Defer, defer, none);

            var r = req.Result;
            var manual = new List<string>();

            if (r.HasError) manual.Add($"ошибка анализа: {r.Error}");
            if (r.RequiresReview) manual.Add("ИИ требует проверки");
            if (!r.AutoCloseEligible && !r.RequiresReview && !r.HasError)
            {
                if (r.AutoCloseBlockers.Length > 0) manual.AddRange(r.AutoCloseBlockers);
                else manual.Add("сервер не подтвердил допуск к автозакрытию");
            }
            // Вопросы к отчёту мастера, которые что-то значат, уже подняли RequiresReview на сервере
            // (hard/модельные замечания эскалируют). Оставшиеся при «без эскалации» — рекомендательные
            // (S2 «частичная наладка > порога — рекомендуется отразить в комментарии»), они не блокируют
            // и у аналитика. Октябрь 2026: 9 суток из 367 блокировались только ими.
            if (r.EscalatedByShiftReport) manual.Add("есть вопросы к отчёту мастера");
            if (r.FlaggedParts.Length > 0) manual.Add("ИИ отметил проблемные строки");
            if (r.Confidence < options.MinConfidence)
                manual.Add($"уверенность {r.Confidence:P0} ниже порога {options.MinConfidence:P0}");

            var allowed = options.AllowedPromptVersions;
            if (allowed is { Count: > 0 }
                && (string.IsNullOrEmpty(r.PromptVersion) || !allowed.Contains(r.PromptVersion)))
                manual.Add($"версия промпта «{r.PromptVersion ?? "—"}» не допущена к автозакрытию");

            if (req.Parts.Any(p => p.NeedUpdate)) manual.Add("есть несохранённые правки строк");

            var exclusions = ResolveExclusions(r, req.Parts, manual);

            return manual.Count > 0
                ? new(AutoCloseOutcome.Manual, manual, none)
                : new(AutoCloseOutcome.Close, Array.Empty<string>(), exclusions);
        }

        /// <summary>
        /// Предложения об исключении из К1 → конкретные строки. Любое непонятное
        /// предложение (не разобралось или не нашлось однозначной строки) — причина
        /// отказа: частично применять нельзя.
        /// </summary>
        private static List<(Part Part, string Reason)> ResolveExclusions(
            AiAnalysisResult r, IReadOnlyList<Part> parts, List<string> manual)
        {
            var result = new List<(Part, string)>();
            var entries = r.SuggestExcludeFromReports ?? Array.Empty<string>();
            if (entries.Length == 0) return result;

            var parsed = AiExcludeSuggestion.ParseMany(entries, r.Explanation);
            if (parsed.Count != entries.Length)
                manual.Add("предложение об исключении не удалось разобрать");

            foreach (var s in parsed)
            {
                var part = AiExcludeSuggestion.FindRow(parts, s);
                if (part == null)
                {
                    manual.Add($"исключение не сопоставлено со строкой: {s.PartName} | М/Л: {s.Order} | Уст.{s.Setup}");
                    continue;
                }
                if (part.ExcludeFromReports || result.Any(x => ReferenceEquals(x.Item1, part))) continue;
                result.Add((part, s.Reason));
            }

            return result;
        }
    }
}

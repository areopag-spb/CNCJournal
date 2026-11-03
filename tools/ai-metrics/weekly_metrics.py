#!/usr/bin/env python3
"""Недельные метрики AiService по request_logs (P3.3 плана исправлений).

Использование:
    python weekly_metrics.py [--logs C:\\AiService\\request_logs] [--days 7] [--endpoint stream]

Читает json-логи запросов за последние N дней (по loggedAt) и печатает: объём, доли эскалаций и
допуска к автозакрытию, причины отказа, сбои цикла агента и ретраи, использование tools и толчка,
токены и упоры в контекст (с P2.1), рассинхрон рассуждения и вердикта. Данные по закрытиям и
контролю аналитиков — в weekly_autoclose.sql (журнал ai_auto_close_log в БД).
Скрипт только читает файлы.
"""
import argparse
import collections
import glob
import json
import os
import statistics
import sys
from datetime import datetime, timedelta

sys.stdout.reconfigure(encoding="utf-8")


def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--logs", default=r"C:\AiService\request_logs")
    ap.add_argument("--days", type=int, default=7)
    ap.add_argument("--endpoint", default="", help="фильтр по endpoint: stream, analyze (пусто — все дневные анализы)")
    return ap.parse_args()


def load(root, since, endpoint):
    rows = []
    for path in glob.glob(os.path.join(root, "*", "*.json")):
        name = os.path.basename(path)
        if name.startswith(("verify_", "error_")):
            continue
        try:
            with open(path, encoding="utf-8") as f:
                d = json.load(f)
            logged = datetime.fromisoformat(str(d.get("loggedAt", ""))[:19])
        except Exception:
            continue
        if logged < since:
            continue
        if endpoint and d.get("endpoint") != endpoint:
            continue
        rows.append(d)
    return rows


def pct(n, total):
    return f"{n} ({n / total:.0%})" if total else "0"


def main():
    args = parse_args()
    since = datetime.now() - timedelta(days=args.days)
    rows = load(args.logs, since, args.endpoint)
    total = len(rows)
    print(f"Период: с {since:%d.%m.%Y %H:%M}, endpoint={args.endpoint or 'все'}, запросов: {total}")
    if not total:
        return

    resp = [r.get("response") or {} for r in rows]
    trace = [r.get("agentTrace") or {} for r in rows]

    rr = sum(1 for x in resp if x.get("requiresReview"))
    elig = sum(1 for x in resp if x.get("autoCloseEligible"))
    err = sum(1 for x in resp if x.get("hasError"))
    print(f"Требует проверки: {pct(rr, total)}; допущено к автозакрытию: {pct(elig, total)}; ошибки ответа: {err}")
    print(f"Эскалация данными (HARD): {pct(sum(1 for x in resp if x.get('escalatedByData')), total)}; "
          f"отчётом мастера: {pct(sum(1 for x in resp if x.get('escalatedByShiftReport')), total)}")

    blockers = collections.Counter(b for x in resp for b in (x.get("autoCloseBlockers") or []))
    if blockers:
        print("\nПричины, по которым сутки без эскалации не допущены к закрытию:")
        for b, n in blockers.most_common(10):
            print(f"  {n:4d}  {b[:110]}")

    agent = [t for t in trace if t]
    print(f"\nАгентский контур: {len(agent)} из {total}")
    if agent:
        print(f"  сбой цикла (fellBack): {pct(sum(1 for t in agent if t.get('fellBack')), len(agent))}; "
              f"ретраи: {sum(t.get('retries') or 0 for t in agent)}; "
              f"толчок к tools: {sum(1 for t in agent if t.get('contextNudged'))}")
        fb = collections.Counter(t.get("fallbackReason") for t in agent if t.get("fellBack"))
        for k, n in fb.most_common(5):
            print(f"    {n:3d}  {k}")
        tools = collections.Counter(r["name"] for t in agent for r in (t.get("rounds") or []))
        print(f"  суток с вызовом tools: {sum(1 for t in agent if t.get('rounds'))}; вызовы: {dict(tools) or '—'}")

        peaks = [t["peakPromptTokens"] for t in agent if t.get("peakPromptTokens")]
        if peaks:
            peaks.sort()
            p95 = peaks[int(len(peaks) * 0.95) - 1] if len(peaks) > 1 else peaks[0]
            print(f"  токены промпта: медиана {int(statistics.median(peaks))}, p95 {p95}, максимум {peaks[-1]} "
                  f"(окно 16384); упоров в контекст: {sum(1 for t in agent if t.get('contextOverflow'))}")
        else:
            print("  токены промпта: в логах нет (служба до P2.1)")

    conflict = sum(1 for x in resp
                   if any("Нестабильный вывод" in s for s in (x.get("signals") or [])))
    print(f"\nРассинхрон рассуждения и вердикта («нестабильный вывод»): {conflict}")

    versions = collections.Counter(r.get("promptVersion") for r in rows)
    print("Версии промпта: " + "; ".join(f"{v} × {n}" for v, n in versions.most_common(4)))

    by_day = collections.Counter(str(r.get("loggedAt"))[:10] for r in rows)
    print("По дням: " + ", ".join(f"{d[5:]}={n}" for d, n in sorted(by_day.items())))


if __name__ == "__main__":
    main()

"""Прогон золотого набора через живой AiService (POST /api/analysis).

Запуск:  python run_golden.py [http://localhost:5051] [out.json]
Сервис поднимать отдельным экземпляром на другом порту (Port=5051), чтобы не трогать прод:
  dotnet build AiService/AiService.csproj -c Release "-p:OutDir=$TEMP/aisvc_golden/"
  Port=5051 ASPNETCORE_ENVIRONMENT=Development $TEMP/aisvc_golden/AiService.exe
Эталон — Decision аналитика (expectedDecision); печатает матрицу «эталон / вердикт ИИ».
Предупреждение: эталон = решение аналитика на момент разбора по правилам того времени,
несовпадение не обязательно ошибка модели — каждый пропуск разбирать вручную.
"""
import glob
import json
import os
import sys
import time
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5051"
out = sys.argv[2] if len(sys.argv) > 2 else "golden_result.json"
here = os.path.dirname(os.path.abspath(__file__))
rows = []

for f in sorted(glob.glob(os.path.join(here, "*.json"))):
    g = json.load(open(f, encoding="utf-8"))
    body = json.dumps(g["request"], ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(base + "/api/analysis", data=body,
                                 headers={"Content-Type": "application/json"})
    t = time.time()
    try:
        r = json.load(urllib.request.urlopen(req, timeout=900))
        ai = r.get("requiresReview")
        err = r.get("error")
    except Exception as e:
        r, ai, err = {}, None, str(e)[:120]
    rows.append({
        "file": os.path.basename(f), "group": g["group"], "expected": g["expectedDecision"],
        "ai": ai, "error": err, "sec": round(time.time() - t, 1),
        "signals": r.get("signals", []), "version": r.get("promptVersion"),
    })
    print(f'{rows[-1]["file"]:60} exp={rows[-1]["expected"]:9} ai={ai} {rows[-1]["sec"]}s', flush=True)
    json.dump(rows, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)


def cell(exp, ai):
    return sum(1 for x in rows if x["expected"] == exp and x["ai"] == ai)


print("\nexpected / AI      escalated   not-escalated   error")
for exp in ("escalated", "ok"):
    err = sum(1 for x in rows if x["expected"] == exp and x["ai"] is None)
    print(f"{exp:18} {cell(exp, True):9} {cell(exp, False):15} {err:7}")
for grp in ("MISS", "ESC", "OVER", "OK"):
    sub = [x for x in rows if x["group"] == grp]
    print(grp, "AI escalated:", sum(1 for x in sub if x["ai"] is True), "of", len(sub))

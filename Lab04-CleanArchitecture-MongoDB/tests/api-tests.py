#!/usr/bin/env python3
"""
Автоматична перевірка критеріїв приймання Лабораторної роботи № 04
для мікросервісу «Відгуки» (Clean Architecture + CQRS/MediatR + MongoDB).

Запуск (API має бути піднятий):
    py -I tests/api-tests.py                      # http://localhost:5590
    py -I tests/api-tests.py http://localhost:5100

Скрипт самоочисний: прибирає за попередніми прогонами на старті.
"""

import json
import random
import sys
import urllib.error
import urllib.parse
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:5590"
SUFFIX = random.randint(100000, 999999)
AUTOTEST_READER = 90000 + random.randint(1, 9000)
# Автотести працюють з ОКРЕМОЮ книгою, якої немає в сідінгу, —
# інакше масові операції (refresh-book-title) зачепили б seed-дані.
AUTOTEST_BOOK = 900000 + random.randint(1, 9000)

passed: list[str] = []
failed: list[str] = []


def call(method: str, path: str, body=None, headers: dict | None = None):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None

    route, sep, query = path.partition("?")
    route = urllib.parse.quote(route, safe="/")
    if sep:
        pairs = []
        for part in query.split("&"):
            k, _, v = part.partition("=")
            pairs.append(f"{k}={urllib.parse.quote(v, safe='')}")
        path = route + "?" + "&".join(pairs)
    else:
        path = route

    all_headers = {"Content-Type": "application/json; charset=utf-8", "Accept": "application/json"}
    if headers:
        all_headers.update(headers)

    req = urllib.request.Request(BASE + path, data=data, method=method, headers=all_headers)
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read().decode("utf-8")
            return r.status, (json.loads(raw) if raw else None), dict(r.headers)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8")
        try:
            return e.code, json.loads(raw), dict(e.headers)
        except json.JSONDecodeError:
            return e.code, raw, dict(e.headers)


def check(name: str, condition: bool, detail: str = ""):
    if condition:
        passed.append(name)
        print(f"  [OK]   {name}")
    else:
        failed.append(f"{name} :: {detail}")
        print(f"  [FAIL] {name}")
        if detail:
            print(f"         {detail}")


def section(title: str):
    print()
    print("=" * 74)
    print(f" {title}")
    print("=" * 74)


# =============================================================================
section("0. Сервіс доступний; індекси та ідемпотентний сідінг виконано")
# =============================================================================
st, body, _ = call("GET", "/health")
check("GET /health -> 200 Healthy",
      st == 200 and body and body.get("status") == "Healthy", f"status={st} body={body}")

# прибирання за попередніми прогонами
_, leftovers, _ = call("GET", "/api/reviews?pageSize=100")
removed = 0
if isinstance(leftovers, dict):
    for r in leftovers.get("items", []):
        if r["bookId"] >= 900000 or (r["author"]["readerId"] or 0) >= 90000:
            call("DELETE", f"/api/reviews/{r['id']}")
            removed += 1
if removed:
    print(f"  [..]   прибрано залишків минулих прогонів: {removed}")

st, page, _ = call("GET", "/api/reviews?pageSize=100")
seeded = page.get("totalCount") if isinstance(page, dict) else -1
check("сідінг виконано: у колекції є відгуки", st == 200 and seeded >= 5,
      f"status={st} totalCount={seeded}")
check("кирилиця в документах збереглася",
      isinstance(page, dict) and any("Кобзар" in r["bookTitle"] for r in page["items"]),
      f"titles={[r['bookTitle'] for r in page.get('items', [])][:6] if isinstance(page, dict) else '?'}")

# =============================================================================
section("1. Структура документа MongoDB зі схеми Лаб. 01")
# =============================================================================
kobzar = next((r for r in page["items"] if r["bookTitle"] == "Кобзар"), None)
check("знайдено відгук на «Кобзар»", kobzar is not None)

st, details, _ = call("GET", f"/api/reviews/{kobzar['id']}") if kobzar else (0, None, {})
check("GET /api/reviews/{id} -> 200", st == 200, f"status={st}")
check("author — ВБУДОВАНИЙ об'єкт {readerId, displayName}",
      isinstance(details, dict) and isinstance(details.get("author"), dict)
      and "displayName" in details["author"],
      f"author={details.get('author') if isinstance(details, dict) else details}")
check("comments — ВБУДОВАНИЙ масив із піддокументами",
      isinstance(details, dict) and len(details.get("comments", [])) == 2,
      f"comments={len(details.get('comments', [])) if isinstance(details, dict) else '?'}")
check("discussionId — ПОСИЛАННЯ на окрему колекцію (гібридний підхід)",
      isinstance(details, dict) and details.get("discussionId"),
      f"discussionId={details.get('discussionId') if isinstance(details, dict) else '?'}")
check("bookTitle денормалізовано (копія з Каталогу)",
      isinstance(details, dict) and details.get("bookTitle") == "Кобзар")
check("Id повертається РЯДКОМ, а не сирим BSON",
      isinstance(details, dict) and isinstance(details.get("id"), str) and len(details["id"]) == 24,
      f"id={details.get('id') if isinstance(details, dict) else '?'}")

st, internat_page, _ = call("GET", "/api/reviews?bookId=4&pageSize=1")
internat_id = internat_page["items"][0]["id"] if internat_page.get("items") else None
st, internat, _ = call("GET", f"/api/reviews/{internat_id}")
check("ГНУЧКА СХЕМА: у цього документа є photos і tags, яких немає в інших",
      isinstance(internat, dict) and len(internat.get("photos", [])) == 2
      and len(internat.get("tags", [])) >= 2,
      f"photos={internat.get('photos') if isinstance(internat, dict) else '?'}")
check("а у відгуку на «Кобзар» цих полів немає (порожні)",
      isinstance(details, dict) and not details.get("photos") and not details.get("tags"))

# =============================================================================
section("2. ВАЛІДАЦІЯ через Pipeline Behavior — ДО виклику обробника")
# =============================================================================
st, body, _ = call("POST", "/api/reviews", {
    "bookId": AUTOTEST_BOOK, "bookTitle": "Автотест", "readerId": AUTOTEST_READER,
    "authorName": "Автотест", "rating": 10, "text": "rating поза межами"
})
check("rating = 10 -> 400 (ValidationBehavior зупинив до обробника)", st == 400, f"status={st}")
check("формат ValidationProblemDetails з розбивкою по полях",
      isinstance(body, dict) and isinstance(body.get("errors"), dict)
      and any("Rating" in k for k in body["errors"]),
      f"errors={body.get('errors') if isinstance(body, dict) else body}")

st, body, _ = call("POST", "/api/reviews", {
    "bookId": 0, "bookTitle": "", "readerId": None, "authorName": "", "rating": 3, "text": "x"
})
check("кілька помилок збираються разом -> 400", st == 400, f"status={st}")
check("повернуто щонайменше 3 поля з помилками",
      isinstance(body, dict) and len(body.get("errors", {})) >= 3,
      f"errors={list(body.get('errors', {}).keys()) if isinstance(body, dict) else body}")

st, body, _ = call("GET", "/api/reviews/not-an-objectid")
check("некоректний ObjectId -> 400 (валідатор ObjectId.TryParse)", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/reviews?pageSize=5000")
check("завеликий pageSize -> 400", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/reviews?minRating=9")
check("minRating поза межами 1..5 -> 400", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/reviews/search?term=a")
check("закороткий пошуковий запит -> 400", st == 400, f"status={st}")

# =============================================================================
section("3. 404 у форматі ProblemDetails")
# =============================================================================
st, body, _ = call("GET", "/api/reviews/000000000000000000000000")
check("неіснуючий ObjectId -> 404", st == 404, f"status={st}")
check("формат ProblemDetails",
      isinstance(body, dict) and {"type", "title", "status"} <= set(body), f"body={body}")

st, body, _ = call("DELETE", "/api/reviews/000000000000000000000000")
check("видалення неіснуючого -> 404", st == 404, f"status={st}")

st, body, _ = call("GET", "/api/discussions/000000000000000000000000")
check("неіснуюче обговорення -> 404", st == 404, f"status={st}")

# =============================================================================
section("4. CQRS: команда створення -> 201 Created з рядковим Id")
# =============================================================================
st, created_id, headers = call("POST", "/api/reviews", {
    "bookId": AUTOTEST_BOOK, "bookTitle": f"Автотест {SUFFIX}", "readerId": AUTOTEST_READER,
    "authorName": "Автотест Читачівський", "rating": 4, "text": "Відгук створено автотестом."
})
check("POST /api/reviews -> 201 Created", st == 201, f"status={st} body={created_id}")
check("є заголовок Location", "Location" in headers, f"headers={list(headers)}")
new_id = created_id if isinstance(created_id, str) else None
check("повернуто рядковий ObjectId", isinstance(new_id, str) and len(new_id) == 24,
      f"id={new_id}")

st, fetched, headers = call("GET", f"/api/reviews/{new_id}")
check("створений відгук читається", st == 200 and fetched.get("rating") == 4, f"status={st}")
check("ETag містить версію документа", headers.get("ETag") == '"0"',
      f"ETag={headers.get('ETag')}")

# =============================================================================
section("5. Бізнес-правило: один читач — один відгук на книгу (асинхронна валідація)")
# =============================================================================
st, body, _ = call("POST", "/api/reviews", {
    "bookId": AUTOTEST_BOOK, "bookTitle": f"Автотест {SUFFIX}", "readerId": AUTOTEST_READER,
    "authorName": "Автотест Читачівський", "rating": 5, "text": "Повторний відгук"
})
check("повторний відгук того самого читача -> 400 або 409, але НЕ 500",
      st in (400, 409), f"status={st} body={body}")
check("причину пояснено",
      isinstance(body, dict) and "залишив відгук" in json.dumps(body, ensure_ascii=False),
      f"body={json.dumps(body, ensure_ascii=False)[:200] if isinstance(body, dict) else body}")

st, anon_id, _ = call("POST", "/api/reviews", {
    "bookId": AUTOTEST_BOOK, "bookTitle": f"Автотест {SUFFIX}", "readerId": None,
    "authorName": "Анонім автотесту", "rating": 3, "text": "Анонімні не обмежуються"
})
check("анонімний відгук на ту саму книгу -> 201 (партіальний індекс їх не ловить)",
      st == 201, f"status={st} body={anon_id}")

# =============================================================================
section("6. ОПТИМІСТИЧНА КОНКУРЕНТНІСТЬ: ETag / If-Match і конфлікт версій")
# =============================================================================
st, _, _ = call("PUT", f"/api/reviews/{new_id}",
                {"rating": 5, "text": "Оновлено з правильною версією", "expectedVersion": None},
                headers={"If-Match": '"0"'})
check("PUT з коректним If-Match -> 204", st == 204, f"status={st}")

st, after, headers = call("GET", f"/api/reviews/{new_id}")
check("версія зросла до 1", after.get("version") == 1, f"version={after.get('version')}")
check("ETag оновився", headers.get("ETag") == '"1"', f"ETag={headers.get('ETag')}")
check("зміни збережено", after.get("rating") == 5, f"rating={after.get('rating')}")

st, body, _ = call("PUT", f"/api/reviews/{new_id}",
                   {"rating": 2, "text": "Застаріла версія", "expectedVersion": None},
                   headers={"If-Match": '"0"'})
check("PUT зі ЗАСТАРІЛИМ If-Match -> 409 Conflict", st == 409, f"status={st}")

st, unchanged, _ = call("GET", f"/api/reviews/{new_id}")
check("документ НЕ змінився після конфлікту", unchanged.get("rating") == 5,
      f"rating={unchanged.get('rating')}")

# =============================================================================
section("7. Вбудовані коментарі та доменні інваріанти")
# =============================================================================
st, _, _ = call("POST", f"/api/reviews/{new_id}/comments",
                {"readerId": None, "authorName": "Коментатор", "text": "Перший коментар"})
check("додавання коментаря -> 204", st == 204, f"status={st}")

st, with_comment, _ = call("GET", f"/api/reviews/{new_id}")
check("коментар зʼявився у вбудованому масиві",
      len(with_comment.get("comments", [])) == 1,
      f"comments={with_comment.get('comments')}")

st, body, _ = call("POST", f"/api/reviews/{new_id}/comments",
                   {"readerId": None, "authorName": "", "text": ""})
check("порожній коментар -> 400 (валідатор)", st == 400, f"status={st}")

# =============================================================================
section("8. Запити: пагінація, курсор, агрегація, повнотекстовий пошук")
# =============================================================================
st, p1, _ = call("GET", "/api/reviews?page=1&pageSize=2")
check("пагінація: сторінка 1 містить 2 записи",
      st == 200 and len(p1.get("items", [])) == 2, f"items={len(p1.get('items', []))}")
check("totalCount — усі документи, а не розмір сторінки",
      p1.get("totalCount", 0) > 2, f"totalCount={p1.get('totalCount')}")
check("метадані пагінації присутні",
      all(k in p1 for k in ("page", "pageSize", "totalPages", "hasNext", "hasPrevious")))

st, p2, _ = call("GET", "/api/reviews?page=2&pageSize=2")
ids1 = {r["id"] for r in p1.get("items", [])}
ids2 = {r["id"] for r in p2.get("items", [])}
check("сторінки не перетинаються (skip/limit виконує MongoDB)", not (ids1 & ids2))

st, c1, _ = call("GET", "/api/reviews/by-cursor?pageSize=2")
check("курсорна пагінація -> 200 із nextCursor",
      st == 200 and len(c1.get("items", [])) == 2 and c1.get("nextCursor"),
      f"body={c1}")
st, c2, _ = call("GET", f"/api/reviews/by-cursor?pageSize=2&cursor={c1['nextCursor']}")
cids1 = {r["id"] for r in c1.get("items", [])}
cids2 = {r["id"] for r in c2.get("items", [])}
check("друга порція курсора не перетинається з першою", not (cids1 & cids2))

st, ratings, _ = call("GET", "/api/reviews/ratings")
check("GET /api/reviews/ratings -> 200 (aggregation pipeline)",
      st == 200 and isinstance(ratings, list) and ratings, f"status={st}")
check("агрегат містить середню оцінку та кількість",
      all(k in ratings[0] for k in ("bookId", "averageRating", "reviewsCount", "commentsTotal")),
      f"row={ratings[0] if ratings else '?'}")

st, found, _ = call("GET", "/api/reviews/search?term=примітками")
check("повнотекстовий пошук через text-індекс -> 200",
      st == 200 and isinstance(found, list), f"status={st} body={found}")
check("знайдено відгук із цим словом (без стемінгу: шукаємо точну форму)", len(found) >= 1, f"count={len(found)}")

st, filtered, _ = call("GET", "/api/reviews?minRating=5")
check("фільтр minRating=5 звужує вибірку",
      st == 200 and all(r["rating"] >= 5 for r in filtered.get("items", [])),
      f"ratings={[r['rating'] for r in filtered.get('items', [])]}")

# =============================================================================
section("9. Обговорення: окрема колекція + referencing")
# =============================================================================
st, disc_id, _ = call("POST", "/api/discussions", {
    "bookId": AUTOTEST_BOOK, "bookTitle": f"Автотест {SUFFIX}", "topic": f"Автотест обговорення {SUFFIX}"
})
check("POST /api/discussions -> 201", st == 201, f"status={st} body={disc_id}")

st, _, _ = call("POST", f"/api/discussions/{disc_id}/messages",
                {"readerId": None, "authorName": "Учасник", "text": "Перше повідомлення"})
check("додано повідомлення -> 204", st == 204, f"status={st}")

st, disc, _ = call("GET", f"/api/discussions/{disc_id}")
check("повідомлення у вбудованому масиві", len(disc.get("messages", [])) == 1,
      f"messages={disc.get('messages')}")

st, _, _ = call("PUT", f"/api/reviews/{new_id}/discussion/{disc_id}")
check("прикріплення обговорення до відгуку -> 204 (гібридна модель)", st == 204, f"status={st}")

st, linked, _ = call("GET", f"/api/reviews/{new_id}")
check("відгук тепер посилається на обговорення",
      linked.get("discussionId") == disc_id, f"discussionId={linked.get('discussionId')}")

st, _, _ = call("POST", f"/api/discussions/{disc_id}/close", {"reason": "автотест"})
check("закриття обговорення -> 204", st == 204, f"status={st}")

st, body, _ = call("POST", f"/api/discussions/{disc_id}/messages",
                   {"readerId": None, "authorName": "Учасник", "text": "Після закриття"})
check("повідомлення в ЗАКРИТЕ обговорення -> 400 (інваріант сутності)",
      st == 400, f"status={st}")

# =============================================================================
section("10. Політика дублювання: оновлення денормалізованої назви")
# =============================================================================
st, count, _ = call("POST", "/api/reviews/refresh-book-title",
                    {"bookId": AUTOTEST_BOOK, "newTitle": f"Автотест {SUFFIX} (оновлено)"})
check("POST /api/reviews/refresh-book-title -> 200", st == 200, f"status={st}")
check("оновлено щонайменше один документ", isinstance(count, int) and count >= 1,
      f"modified={count}")

st, renamed, _ = call("GET", f"/api/reviews/{new_id}")
check("денормалізована назва оновилася",
      renamed.get("bookTitle") == f"Автотест {SUFFIX} (оновлено)",
      f"bookTitle={renamed.get('bookTitle')}")

# =============================================================================
section("11. Видалення та Swagger")
# =============================================================================
st, _, _ = call("DELETE", f"/api/reviews/{new_id}")
check("DELETE -> 204", st == 204, f"status={st}")
st, _, _ = call("GET", f"/api/reviews/{new_id}")
check("видалений відгук -> 404", st == 404, f"status={st}")

if isinstance(anon_id, str):
    call("DELETE", f"/api/reviews/{anon_id}")

st, spec, _ = call("GET", "/swagger/v1/swagger.json")
check("GET /swagger/v1/swagger.json -> 200", st == 200, f"status={st}")
check("описано reviews і discussions",
      isinstance(spec, dict) and all(
          any(p.startswith(x) for p in spec.get("paths", {}))
          for x in ("/api/reviews", "/api/discussions")),
      f"paths={list(spec.get('paths', {}))[:8] if isinstance(spec, dict) else '?'}")

# =============================================================================
print()
print("=" * 74)
print(f" РЕЗУЛЬТАТ: {len(passed)} пройдено, {len(failed)} провалено")
print("=" * 74)
if failed:
    print("\nПровалені перевірки:")
    for f in failed:
        print(f"  - {f}")
    sys.exit(1)
print("\nУсі критерії приймання Лабораторної роботи № 04 виконано.")
sys.exit(0)

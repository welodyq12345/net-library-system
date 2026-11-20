#!/usr/bin/env python3
"""
Автоматична перевірка критеріїв приймання Лабораторної роботи № 03
для мікросервісу «Каталог книг» (Library.Catalog.Api, EF Core).

Запуск (API має бути піднятий):
    py -I tests/api-tests.py                      # http://localhost:5290
    py -I tests/api-tests.py http://localhost:5100

Кожен блок відповідає пункту розділу «Перед здачею перевірте себе».
Скрипт самодостатній: лише стандартна бібліотека.
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

BASE = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:5290"
SUFFIX = random.randint(1000, 9999)

passed: list[str] = []
failed: list[str] = []


def call(method: str, path: str, body=None):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None

    # сегменти шляху (можуть містити кирилицю) і значення query-параметрів
    # (можуть містити пробіли та свідомо «небезпечні» символи) кодуємо окремо
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

    req = urllib.request.Request(
        BASE + path, data=data, method=method,
        headers={"Content-Type": "application/json; charset=utf-8", "Accept": "application/json"},
    )
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
    print("=" * 72)
    print(f" {title}")
    print("=" * 72)


# =============================================================================
section("0. Сервіс доступний, міграції застосовано, сідінг виконано")
# =============================================================================
st, body, _ = call("GET", "/health")
check("GET /health -> 200 Healthy", st == 200 and body and body.get("status") == "Healthy",
      f"status={st} body={body}")

# --- Прибирання за попередніми прогонами ---------------------------------
# Скрипт створює книги та авторів із префіксом «Автотест». Щоб його можна було
# запускати багато разів поспіль, спершу прибираємо залишки минулих прогонів.
_, leftovers, _ = call("GET", "/api/books?search=Автотест&pageSize=100")
removed_books = 0
if isinstance(leftovers, dict):
    for b in leftovers.get("items", []):
        if b["title"].startswith("Автотест"):
            call("DELETE", f"/api/books/{b['id']}")
            removed_books += 1

_, all_authors, _ = call("GET", "/api/authors")
removed_authors = 0
if isinstance(all_authors, list):
    for a in all_authors:
        if a["fullName"].startswith("Автотест Авторенко"):
            call("DELETE", f"/api/authors/{a['id']}")
            removed_authors += 1

if removed_books or removed_authors:
    print(f"  [..]   прибрано залишки минулих прогонів: "
          f"{removed_books} книг(и), {removed_authors} автор(ів)")

st, authors, _ = call("GET", "/api/authors")
check("статичний сідінг (HasData): 6 авторів",
      st == 200 and isinstance(authors, list) and len(authors) == 6,
      f"status={st} count={len(authors) if isinstance(authors, list) else '?'}")

st, genres, _ = call("GET", "/api/genres")
check("статичний сідінг (HasData): 5 жанрів",
      st == 200 and isinstance(genres, list) and len(genres) == 5,
      f"status={st} count={len(genres) if isinstance(genres, list) else '?'}")

st, page, _ = call("GET", "/api/books?page=1&pageSize=100")
seeded_total = page.get("totalCount") if isinstance(page, dict) else -1
seeded_isbns = {b["isbn"] for b in page.get("items", [])} if isinstance(page, dict) else set()
expected_isbns = {f"978-966-03-5555-{i}" for i in range(1, 7)}
check("програмний сідінг (DbInitializer): усі 6 книг на місці",
      st == 200 and expected_isbns <= seeded_isbns,
      f"status={st} missing={sorted(expected_isbns - seeded_isbns)}")

check("кирилиця в даних збереглася",
      isinstance(page, dict) and any("Кобзар" in b["title"] for b in page.get("items", [])),
      f"titles={[b['title'] for b in page.get('items', [])] if isinstance(page, dict) else '?'}")

# =============================================================================
section("1. ПАГІНАЦІЯ: totalCount — це ВСІ записи, а не кількість на сторінці")
# =============================================================================
st, p2, headers = call("GET", "/api/books?page=2&pageSize=5")
check("GET /api/books?page=2&pageSize=5 -> 200", st == 200, f"status={st}")
check("totalCount дорівнює загальній кількості книг у БД (6), а не 1",
      isinstance(p2, dict) and p2.get("totalCount") == seeded_total,
      f"totalCount={p2.get('totalCount') if isinstance(p2, dict) else '?'} expected={seeded_total}")
expected_on_page2 = max(0, min(5, seeded_total - 5))
check(f"на другій сторінці рівно {expected_on_page2} запис(ів) ({seeded_total} книг / 5 на сторінку)",
      isinstance(p2, dict) and len(p2.get("items", [])) == expected_on_page2,
      f"items={len(p2.get('items', [])) if isinstance(p2, dict) else '?'}")
check("метадані пагінації присутні",
      isinstance(p2, dict) and all(k in p2 for k in ("page", "pageSize", "totalPages", "hasNext", "hasPrevious")),
      f"keys={list(p2.keys()) if isinstance(p2, dict) else '?'}")
expected_has_next = seeded_total > 10
check(f"hasPrevious=True, hasNext={expected_has_next}",
      isinstance(p2, dict) and p2.get("hasPrevious") is True
      and p2.get("hasNext") is expected_has_next,
      f"hasPrevious={p2.get('hasPrevious')} hasNext={p2.get('hasNext')}")
check("метадані продубльовано в заголовках X-Pagination-*",
      headers.get("X-Pagination-TotalCount") == str(seeded_total),
      f"header={headers.get('X-Pagination-TotalCount')}")

st, p1, _ = call("GET", "/api/books?page=1&pageSize=5")
ids_p1 = {b["id"] for b in p1.get("items", [])} if isinstance(p1, dict) else set()
ids_p2 = {b["id"] for b in p2.get("items", [])} if isinstance(p2, dict) else set()
check("сторінки не перетинаються (Skip/Take виконує БД)",
      len(ids_p1 & ids_p2) == 0 and len(ids_p1) == 5,
      f"p1={sorted(ids_p1)} p2={sorted(ids_p2)}")

# =============================================================================
section("2. ФІЛЬТРАЦІЯ та СОРТУВАННЯ на рівні БД")
# =============================================================================
st, r, _ = call("GET", "/api/books?search=Кобзар")
check("фільтр ?search= (кирилиця) повертає 1 книгу",
      st == 200 and r.get("totalCount") == 1,
      f"status={st} totalCount={r.get('totalCount') if isinstance(r, dict) else r}")

st, r, _ = call("GET", "/api/books?minFee=5")
check("фільтр ?minFee=5 звужує вибірку",
      st == 200 and 0 < r.get("totalCount", 99) < seeded_total,
      f"totalCount={r.get('totalCount') if isinstance(r, dict) else '?'}")

st, r, _ = call("GET", "/api/books?minFee=4&maxFee=5")
fees = [b["dailyFee"] for b in r.get("items", [])] if isinstance(r, dict) else []
check("діапазон ?minFee=4&maxFee=5 дотримано",
      st == 200 and all(4 <= f <= 5 for f in fees) and fees,
      f"fees={fees}")

st, asc, _ = call("GET", "/api/books?sortBy=fee&sortDir=asc&pageSize=100")
st, desc, _ = call("GET", "/api/books?sortBy=fee&sortDir=desc&pageSize=100")
fees_asc = [b["dailyFee"] for b in asc.get("items", [])] if isinstance(asc, dict) else []
fees_desc = [b["dailyFee"] for b in desc.get("items", [])] if isinstance(desc, dict) else []
check("сортування за тарифом за зростанням", fees_asc == sorted(fees_asc), f"{fees_asc}")
check("сортування за тарифом за спаданням", fees_desc == sorted(fees_desc, reverse=True), f"{fees_desc}")

st, r, _ = call("GET", "/api/books?genre=Поезія")
check("фільтр за жанром через зв'язок M:N",
      st == 200 and r.get("totalCount", 0) >= 1,
      f"totalCount={r.get('totalCount') if isinstance(r, dict) else '?'}")

st, r, _ = call("GET", "/api/books?authorId=1")
author1_books = r.get("items", []) if isinstance(r, dict) else []
check("фільтр за автором повертає лише книги цього автора",
      st == 200 and author1_books and all(b["authorId"] == 1 for b in author1_books),
      f"authorIds={[b['authorId'] for b in author1_books]}")

# =============================================================================
section("3. ВАЛІДАЦІЯ: 400 з переліком помилок по полях, а не 500")
# =============================================================================
st, body, _ = call("POST", "/api/books", {
    "title": "", "isbn": "bad-isbn", "dailyFee": 0, "authorId": 0
})
check("POST з порожнім Name -> 400 (а не 500)", st == 400, f"status={st}")
errors = body.get("errors") if isinstance(body, dict) else None
check("помилки розбиті по полях",
      isinstance(errors, dict) and len(errors) >= 3,
      f"errors={errors}")
check("формат ValidationProblemDetails",
      isinstance(body, dict) and body.get("title") and "status" in body,
      f"body={body}")

st, body, _ = call("POST", "/api/books", {
    "title": "Тест формату ISBN", "isbn": "1234567890", "dailyFee": 5, "authorId": 1
})
check("FluentValidation: некоректний формат ISBN -> 400", st == 400, f"status={st}")
check("повідомлення валідатора українською",
      isinstance(body, dict) and "Isbn" in str(body.get("errors", {})),
      f"errors={body.get('errors') if isinstance(body, dict) else body}")

st, body, _ = call("POST", "/api/books", {
    "title": "Взаємозалежні поля", "isbn": f"978-966-03-{SUFFIX}-1",
    "dailyFee": 5, "authorId": 1, "publisher": "Видавництво без опису"
})
check("FluentValidation: publisher без description -> 400", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/books?sortBy=drop_table")
check("недозволене поле сортування -> 400", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/books?pageSize=5000")
check("завеликий pageSize -> 400", st == 400, f"status={st}")

st, body, _ = call("GET", "/api/books?minFee=50&maxFee=10")
check("maxFee < minFee -> 400", st == 400, f"status={st}")

# =============================================================================
section("4. 404 у форматі ProblemDetails, а не 500")
# =============================================================================
st, body, _ = call("GET", "/api/books/999999")
check("GET /api/books/999999 -> 404", st == 404, f"status={st}")
check("формат ProblemDetails",
      isinstance(body, dict) and {"type", "title", "status"} <= set(body),
      f"body={body}")

st, body, _ = call("GET", "/api/authors/999999")
check("GET /api/authors/999999 -> 404", st == 404, f"status={st}")

st, body, _ = call("POST", "/api/books/999999/add-stock", {"quantity": 1})
check("операція над неіснуючою книгою -> 404", st == 404, f"status={st}")

# =============================================================================
section("5. КОНФЛІКТ 409: повторний ISBN та інваріанти сутності")
# =============================================================================
isbn = f"978-966-03-{SUFFIX}-7"
st, created, headers = call("POST", "/api/books", {
    "title": f"Автотест {SUFFIX}", "isbn": isbn, "dailyFee": 7.25,
    "authorId": 1, "publishedYear": 2020,
    "description": "Книга, створена автотестом Лаб. 03.",
    "publisher": "Тестове видавництво", "pageCount": 123,
    "genreIds": [3, 4],
})
check("POST /api/books -> 201 Created", st == 201, f"status={st} body={created}")
check("є заголовок Location", "Location" in headers, f"headers={list(headers.keys())}")
new_id = created.get("id") if isinstance(created, dict) else None
check("жанри призначено через зв'язок M:N",
      isinstance(created, dict) and len(created.get("genres", [])) == 2,
      f"genres={created.get('genres') if isinstance(created, dict) else '?'}")
check("опис створено (зв'язок 1:1)",
      isinstance(created, dict) and created.get("detail") is not None,
      f"detail={created.get('detail') if isinstance(created, dict) else '?'}")

st, body, _ = call("POST", "/api/books", {
    "title": "Дублікат ISBN", "isbn": isbn, "dailyFee": 3,
    "authorId": 1, "description": "дублікат", "publisher": "x", "pageCount": 10,
})
check("ПОВТОРНЕ створення з тим самим ISBN -> 409", st == 409, f"status={st}")
check("повідомлення пояснює конфлікт",
      isinstance(body, dict) and "ISBN" in str(body.get("detail", "")),
      f"detail={body.get('detail') if isinstance(body, dict) else body}")

st, body, _ = call("POST", "/api/books", {
    "title": "Неіснуючий автор", "isbn": f"978-966-03-{SUFFIX}-8",
    "dailyFee": 3, "authorId": 9999,
})
check("створення з неіснуючим автором -> 404", st == 404, f"status={st}")

# =============================================================================
section("6. ІНВАРІАНТИ СУТНОСТІ (бізнес-правила в методах, а не в setter-ах)")
# =============================================================================
st, book, _ = call("GET", f"/api/books/{new_id}")
copies_before = book.get("availableCopies", 0) if isinstance(book, dict) else 0
check("нова книга без примірників має availableCopies = 0", copies_before == 0,
      f"availableCopies={copies_before}")

st, body, _ = call("POST", f"/api/books/{new_id}/remove-stock", {"quantity": 5})
check("Book.RemoveStock при нульовому залишку -> 409", st == 409, f"status={st}")
check("повідомлення — з методу сутності",
      isinstance(body, dict) and "available" in str(body.get("detail", "")).lower(),
      f"detail={body.get('detail') if isinstance(body, dict) else body}")

st, body, _ = call("POST", f"/api/books/{new_id}/copies", {
    "inventoryNumber": f"AT-{SUFFIX}-1", "shelf": "Z-01", "condition": "New"
})
check("додавання примірника -> 200 і +1 до залишку",
      st == 200 and body.get("availableCopies") == 1,
      f"status={st} availableCopies={body.get('availableCopies') if isinstance(body, dict) else '?'}")
check("примірник зʼявився у списку (зв'язок 1:N)",
      isinstance(body, dict) and len(body.get("copies", [])) == 1,
      f"copies={body.get('copies') if isinstance(body, dict) else '?'}")

st, body, _ = call("POST", f"/api/books/{new_id}/copies", {
    "inventoryNumber": f"AT-{SUFFIX}-1", "shelf": "Z-02", "condition": "Good"
})
check("повторний інвентарний номер -> 409 (інваріант сутності)", st == 409, f"status={st}")

st, body, _ = call("POST", f"/api/books/{new_id}/copies", {
    "inventoryNumber": "нелатиниця", "condition": "Good"
})
check("інвентарний номер не за шаблоном -> 400 (FluentValidation)", st == 400, f"status={st}")

st, body, _ = call("POST", f"/api/books/{new_id}/copies", {
    "inventoryNumber": f"AT-{SUFFIX}-2", "condition": "Броньований"
})
check("невідомий стан примірника -> 400", st == 400, f"status={st}")

st, body, _ = call("PUT", f"/api/books/{new_id}", {"title": "Перейменовано", "dailyFee": 0})
check("тариф 0 -> 400 (DataAnnotations/FluentValidation)", st == 400, f"status={st}")

st, body, _ = call("POST", f"/api/books/{new_id}/genres", {"genreId": 3, "isPrimary": False})
check("повторне призначення того самого жанру -> 409", st == 409, f"status={st}")

st, body, _ = call("POST", f"/api/books/{new_id}/genres", {"genreId": 9999})
check("призначення неіснуючого жанру -> 404", st == 404, f"status={st}")

# =============================================================================
section("7. EAGER vs EXPLICIT LOADING")
# =============================================================================
st, book, _ = call("GET", "/api/books/1")
check("Eager Loading: автор, опис, примірники та жанри — в одній відповіді",
      st == 200 and book.get("authorName") and book.get("detail")
      and len(book.get("copies", [])) >= 1 and len(book.get("genres", [])) >= 1,
      f"authorName={book.get('authorName')} detail={bool(book.get('detail'))} "
      f"copies={len(book.get('copies', []))} genres={book.get('genres')}")

st, a_no, _ = call("GET", "/api/authors/1?includeBooks=false")
check("Explicit Loading вимкнено: книги не завантажено",
      st == 200 and len(a_no.get("books", [])) == 0,
      f"books={len(a_no.get('books', [])) if isinstance(a_no, dict) else '?'}")

st, a_yes, _ = call("GET", "/api/authors/1?includeBooks=true")
check("Explicit Loading увімкнено: книги довантажено ДРУГИМ запитом",
      st == 200 and len(a_yes.get("books", [])) >= 1,
      f"books={len(a_yes.get('books', [])) if isinstance(a_yes, dict) else '?'}")

# =============================================================================
section("8. LINQ через проміжну сутність M:N (власне поле AddedAt)")
# =============================================================================
st, recent, _ = call("GET", "/api/genres/Поезія/recent-books?since=2000-01-01T00:00:00Z")
check("GET /api/genres/{name}/recent-books -> 200",
      st == 200 and isinstance(recent, list), f"status={st} body={recent}")
check("повернуто книги жанру «Поезія»",
      isinstance(recent, list) and len(recent) >= 1,
      f"count={len(recent) if isinstance(recent, list) else '?'}")

st, future, _ = call("GET", "/api/genres/Поезія/recent-books?since=2099-01-01T00:00:00Z")
check("фільтр за AddedAt працює: з майбутньої дати — порожньо",
      st == 200 and isinstance(future, list) and len(future) == 0,
      f"count={len(future) if isinstance(future, list) else '?'}")

st, stats, _ = call("GET", "/api/genres/stats")
check("GET /api/genres/stats -> 200 (GROUP BY виконує БД)",
      st == 200 and isinstance(stats, list) and stats, f"status={st}")
check("у статистиці є назва жанру й кількість книг",
      isinstance(stats, list) and stats and "genreName" in stats[0] and "bookCount" in stats[0],
      f"row={stats[0] if isinstance(stats, list) and stats else '?'}")

# =============================================================================
section("9. Друга й третя специфікації через той самий WithSpecification()")
# =============================================================================
st, oos, _ = call("GET", "/api/books/out-of-stock")
check("GET /api/books/out-of-stock -> 200 (друга специфікація)",
      st == 200 and isinstance(oos, list), f"status={st}")
check("нова книга без примірників більше не в списку (їй додали примірник)",
      isinstance(oos, list) and all(b["id"] != new_id for b in oos),
      f"ids={[b['id'] for b in oos] if isinstance(oos, list) else '?'}")

st, by_genre, _ = call("GET", "/api/books/by-genre/poetry?take=5")
check("GET /api/books/by-genre/poetry -> 200 (третя специфікація)",
      st == 200 and isinstance(by_genre, list) and len(by_genre) >= 1,
      f"status={st} count={len(by_genre) if isinstance(by_genre, list) else '?'}")

st, empty, _ = call("GET", "/api/books/by-genre/no-such-slug")
check("неіснуючий slug -> 200 і порожній список",
      st == 200 and isinstance(empty, list) and len(empty) == 0,
      f"status={st} count={len(empty) if isinstance(empty, list) else '?'}")

# =============================================================================
section("10. Автори: створення, конфлікт, інваріант видалення")
# =============================================================================
st, author, _ = call("POST", "/api/authors", {
    "fullName": f"Автотест Авторенко {SUFFIX}", "country": "Україна", "birthYear": 1980
})
check("POST /api/authors -> 201 Created", st == 201, f"status={st} body={author}")
author_id = author.get("id") if isinstance(author, dict) else None

st, body, _ = call("POST", "/api/authors", {"fullName": f"Автотест Авторенко {SUFFIX}"})
check("повторне імʼя автора -> 409", st == 409, f"status={st}")

st, body, _ = call("DELETE", f"/api/authors/{author_id}")
check("видалення автора БЕЗ книг -> 204", st == 204, f"status={st}")

st, body, _ = call("DELETE", "/api/authors/1")
check("видалення автора З книгами -> 409 (інваріант сутності)", st == 409, f"status={st}")
check("повідомлення пояснює причину",
      isinstance(body, dict) and "book" in str(body.get("detail", "")).lower(),
      f"detail={body.get('detail') if isinstance(body, dict) else body}")

# =============================================================================
section("11. М'яке видалення книги")
# =============================================================================
st, _, _ = call("DELETE", f"/api/books/{new_id}")
check("DELETE /api/books/{id} -> 204", st == 204, f"status={st}")

st, body, _ = call("GET", f"/api/books/{new_id}")
check("видалена книга більше не читається -> 404", st == 404, f"status={st}")

st, page_after, _ = call("GET", "/api/books?pageSize=100")
check("видалена книга зникла зі списку",
      isinstance(page_after, dict) and all(b["id"] != new_id for b in page_after.get("items", [])),
      f"ids={[b['id'] for b in page_after.get('items', [])] if isinstance(page_after, dict) else '?'}")
check("інші книги на місці (видалення саме м'яке, не каскадне)",
      isinstance(page_after, dict) and page_after.get("totalCount") == seeded_total,
      f"totalCount={page_after.get('totalCount') if isinstance(page_after, dict) else '?'}")

# =============================================================================
section("12. Swagger / OpenAPI")
# =============================================================================
st, spec, _ = call("GET", "/swagger/v1/swagger.json")
check("GET /swagger/v1/swagger.json -> 200", st == 200, f"status={st}")
check("описано всі три групи ендпоінтів",
      isinstance(spec, dict) and all(
          any(p.startswith(pref) for p in spec.get("paths", {}))
          for pref in ("/api/books", "/api/authors", "/api/genres")),
      f"paths={list(spec.get('paths', {}).keys()) if isinstance(spec, dict) else '?'}")

# =============================================================================
print()
print("=" * 72)
print(f" РЕЗУЛЬТАТ: {len(passed)} пройдено, {len(failed)} провалено")
print("=" * 72)
if failed:
    print("\nПровалені перевірки:")
    for f in failed:
        print(f"  - {f}")
    sys.exit(1)
print("\nУсі критерії приймання Лабораторної роботи № 03 виконано.")
sys.exit(0)

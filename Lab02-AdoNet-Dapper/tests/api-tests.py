#!/usr/bin/env python3
"""
Автоматична перевірка критеріїв приймання Лабораторної роботи № 02
для мікросервісу «Видача книг» (Library.Lending.Api).

Запуск (API має бути піднятий):
    py -I tests/api-tests.py                      # http://localhost:5190
    py -I tests/api-tests.py http://localhost:5100

Кожен тест відповідає пункту розділу «Перед здачею перевірте себе».
Скрипт самодостатній: використовує лише стандартну бібліотеку.
"""

import json
import random
import sys
import urllib.error
import urllib.parse
import urllib.request

SUFFIX = random.randint(100000, 999999)

# консоль Windows за замовчуванням не UTF-8 — інакше кириличні звіти нечитабельні
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:5190"

passed: list[str] = []
failed: list[str] = []


def call(method: str, path: str, body=None):
    """Повертає (status_code, parsed_json_or_text)."""
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None

    # query-параметри кодуємо: вони можуть містити пробіли, кирилицю
    # та свідомо «небезпечні» символи (тест на SQL-ін'єкцію)
    if "?" in path:
        route, _, query = path.partition("?")
        pairs = []
        for part in query.split("&"):
            k, _, v = part.partition("=")
            pairs.append(f"{k}={urllib.parse.quote(v, safe='')}")
        path = route + "?" + "&".join(pairs)

    req = urllib.request.Request(
        BASE + path,
        data=data,
        method=method,
        headers={"Content-Type": "application/json; charset=utf-8",
                 "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read().decode("utf-8")
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8")
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, raw


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
    print("=" * 70)
    print(f" {title}")
    print("=" * 70)


# =============================================================================
section("0. Сервіс доступний, база досяжна")
# =============================================================================
st, body = call("GET", "/health")
check("GET /health -> 200 Healthy", st == 200 and body and body.get("status") == "Healthy",
      f"status={st} body={body}")

# --- Передумови -------------------------------------------------------------
# Скрипт ЗМІНЮЄ стан бази (видає, повертає, оплачує пеню), тому розрахований
# на СВІЖО ЗАСІЯНУ базу. Перевіряємо це одразу й даємо точну інструкцію.
_, l2 = call("GET", "/api/loans/2")
_, l3 = call("GET", "/api/loans/3")
if not (isinstance(l2, dict) and l2.get("status") == "Issued"
        and isinstance(l3, dict) and l3.get("status") == "Returned"):
    print()
    print("  !! ПЕРЕДУМОВУ НЕ ВИКОНАНО: база не у вихідному seed-стані.")
    print(f"     Очікувалось: видача 2 = Issued, видача 3 = Returned.")
    print(f"     Фактично:    видача 2 = {l2.get('status') if isinstance(l2, dict) else l2}, "
          f"видача 3 = {l3.get('status') if isinstance(l3, dict) else l3}.")
    print()
    print("     Перезасійте базу й повторіть:")
    print('       docker exec netlabs-sql /opt/mssql-tools18/bin/sqlcmd -S localhost '
          '-U sa -P "LabsDev!Pass2026" -C -I -f 65001 -i /tmp/lib-p1/schema.sql '
          '-i /tmp/lib-p1/procedures.sql -i /tmp/lib-p1/seed.sql')
    sys.exit(2)
print("  [OK]   передумови: база у вихідному seed-стані")
passed.append("передумови seed-стану")

# =============================================================================
section("1. Читання: локальна копія довідника та видача з позиціями")
# =============================================================================
st, books = call("GET", "/api/books")
check("GET /api/books -> 200 і непорожній список", st == 200 and isinstance(books, list) and books,
      f"status={st}")

st, book = call("GET", "/api/books/1")
check("GET /api/books/1 -> 200, кирилиця збереглася",
      st == 200 and book and "Кобзар" in book.get("title", ""),
      f"status={st} title={book.get('title') if isinstance(book, dict) else book}")

st, loan = call("GET", "/api/loans/3")
check("GET /api/loans/3 -> 200 з деталями та позиціями (Dapper multi-mapping)",
      st == 200 and loan and loan.get("detail") is not None and len(loan.get("items", [])) == 2,
      f"status={st} items={len(loan.get('items', [])) if isinstance(loan, dict) else '?'}")

check("знімок полів каталогу присутній у позиціях",
      st == 200 and all(i.get("bookTitle") and i.get("dailyFee") for i in loan.get("items", [])),
      "у позиціях немає знімків BookTitle/DailyFee")

check("пеню нараховано процедурою (64.75 = 7 днів x 9.25)",
      st == 200 and abs(float(loan.get("fineAmount", 0)) - 64.75) < 0.01,
      f"fineAmount={loan.get('fineAmount') if isinstance(loan, dict) else '?'}")

# =============================================================================
section("2. Неіснуючий Id -> 404 у форматі ProblemDetails, а НЕ 500")
# =============================================================================
st, body = call("GET", "/api/loans/999999")
check("GET /api/loans/999999 -> 404", st == 404, f"status={st}")
check("відповідь у форматі ProblemDetails",
      isinstance(body, dict) and {"type", "title", "status"} <= set(body),
      f"body={body}")

st, body = call("GET", "/api/readers/999999")
check("GET /api/readers/999999 -> 404", st == 404, f"status={st}")

st, body = call("GET", "/api/books/999999")
check("GET /api/books/999999 -> 404", st == 404, f"status={st}")

# =============================================================================
section("3. Недопустимий перехід статусу -> 409 Conflict (THROW 50002)")
# =============================================================================
st, body = call("POST", "/api/loans/3/confirm")
check("підтвердити вже ПОВЕРНЕНУ видачу -> 409", st == 409, f"status={st}")
check("деталі помилки прийшли з процедури",
      isinstance(body, dict) and "only for a new loan" in str(body.get("detail", "")),
      f"detail={body.get('detail') if isinstance(body, dict) else body}")

st, body = call("POST", "/api/loans/2/issue")
check("видати вже ВИДАНУ на руки видачу -> 409", st == 409, f"status={st}")

st, body = call("POST", "/api/loans/2/cancel", {"reason": "перевірка"})
check("скасувати видану на руки видачу -> 409", st == 409, f"status={st}")

st, body = call("POST", "/api/loans/3/return", {})
check("повернути вже повернену видачу -> 409", st == 409, f"status={st}")

# =============================================================================
section("4. Валідація -> 400 Bad Request з переліком помилок по полях")
# =============================================================================
st, body = call("POST", "/api/loans", {
    "readerId": 1, "dueDate": "2026-12-31", "pickupBranch": "", "items": []
})
check("порожні pickupBranch та items -> 400", st == 400, f"status={st}")
check("помилки розбиті по полях",
      isinstance(body, dict) and isinstance(body.get("errors"), dict)
      and len(body["errors"]) >= 2,
      f"errors={body.get('errors') if isinstance(body, dict) else body}")

st, body = call("POST", "/api/readers", {
    "cardNumber": "X", "email": "not-an-email", "fullName": ""
})
check("некоректний email і короткі поля -> 400", st == 400, f"status={st}")

st, body = call("GET", "/api/readers/1/loans?status=NoSuchStatus")
check("невідомий статус у query -> 400 (бізнес-валідація BLL)", st == 400, f"status={st}")

# =============================================================================
section("5. ВІДКАТ ТРАНЗАКЦІЇ: брак примірників нічого не змінює")
# =============================================================================
st, book6_before = call("GET", "/api/books/6")
copies_before = book6_before["copiesAvailable"]
st, loans_before = call("GET", "/api/readers/1/loans")
loans_count_before = len(loans_before)

st, body = call("POST", "/api/loans", {
    "readerId": 1,
    "dueDate": "2026-12-31",
    "pickupBranch": "Центральна філія",
    "items": [{"bookId": 6, "units": 99}],
})
check("замовити 99 примірників при 1 наявному -> 409", st == 409, f"status={st} body={body}")

st, book6_after = call("GET", "/api/books/6")
check("залишок примірників НЕ змінився",
      book6_after["copiesAvailable"] == copies_before,
      f"{copies_before} -> {book6_after['copiesAvailable']}")

st, loans_after = call("GET", "/api/readers/1/loans")
check("нову видачу НЕ створено (транзакція відкотилася)",
      len(loans_after) == loans_count_before,
      f"{loans_count_before} -> {len(loans_after)}")

# =============================================================================
section("6. Повний цикл видачі: створення -> видача -> повернення")
# =============================================================================
st, created = call("POST", "/api/loans", {
    "readerId": 1,
    "dueDate": "2026-12-31",
    "pickupBranch": "Центральна філія",
    "comment": "Автотест повного циклу",
    "items": [{"bookId": 1, "units": 1}, {"bookId": 2, "units": 1}],
})
check("POST /api/loans -> 201 Created", st == 201, f"status={st} body={created}")
new_id = created.get("id") if isinstance(created, dict) else None
check("статус нової видачі = Submitted",
      isinstance(created, dict) and created.get("status") == "Submitted",
      f"status={created.get('status') if isinstance(created, dict) else '?'}")
check("позиції отримали ЗНІМКИ назви й тарифу з довідника",
      isinstance(created, dict) and len(created.get("items", [])) == 2
      and all(i["bookTitle"] and i["dailyFee"] > 0 for i in created["items"]),
      f"items={created.get('items') if isinstance(created, dict) else '?'}")

st, b1_before = call("GET", "/api/books/1")
copies1_before = b1_before["copiesAvailable"]

st, issued = call("POST", f"/api/loans/{new_id}/issue")
check("POST /{id}/issue -> 200, статус Issued",
      st == 200 and issued.get("status") == "Issued",
      f"status={st} loanStatus={issued.get('status') if isinstance(issued, dict) else '?'}")

st, b1_after = call("GET", "/api/books/1")
check("залишок примірників зменшився на 1 (транзакція спрацювала)",
      b1_after["copiesAvailable"] == copies1_before - 1,
      f"{copies1_before} -> {b1_after['copiesAvailable']}")

st, history = call("GET", f"/api/loans/{new_id}/history")
check("журнал статусів містить усі переходи",
      st == 200 and len(history) >= 3,
      f"status={st} entries={len(history) if isinstance(history, list) else '?'}")

st, returned = call("POST", f"/api/loans/{new_id}/return", {})
check("POST /{id}/return -> 200, статус Returned",
      st == 200 and returned.get("status") == "Returned",
      f"status={st} loanStatus={returned.get('status') if isinstance(returned, dict) else '?'}")
check("пеня = 0 (повернено в термін)",
      float(returned.get("fineAmount", -1)) == 0,
      f"fineAmount={returned.get('fineAmount') if isinstance(returned, dict) else '?'}")

st, b1_final = call("GET", "/api/books/1")
check("примірники повернулися в довідник",
      b1_final["copiesAvailable"] == copies1_before,
      f"{copies1_before} -> {b1_final['copiesAvailable']}")

# =============================================================================
section("7. Скасування до видачі на руки — допустиме")
# =============================================================================
st, c = call("POST", "/api/loans", {
    "readerId": 2, "dueDate": "2026-12-31", "pickupBranch": "Філія №2",
    "items": [{"bookId": 5, "units": 1}],
})
check("створено видачу для скасування -> 201", st == 201, f"status={st}")
cancel_id = c.get("id") if isinstance(c, dict) else None

st, cancelled = call("POST", f"/api/loans/{cancel_id}/cancel", {"reason": "Читач передумав"})
check("скасувати НОВУ видачу -> 200, статус Cancelled",
      st == 200 and cancelled.get("status") == "Cancelled",
      f"status={st} loanStatus={cancelled.get('status') if isinstance(cancelled, dict) else '?'}")

# =============================================================================
section("8. Спецсимвол в даних (O'Brien) — параметризація, не SQL-ін'єкція")
# =============================================================================
st, readers = call("GET", "/api/readers")
obrien = [r for r in readers if "Brien" in r.get("fullName", "")] if isinstance(readers, list) else []
check("читач Liam O'Brien читається без синтаксичної помилки SQL",
      len(obrien) == 1 and obrien[0]["fullName"] == "Liam O'Brien",
      f"found={obrien}")

# свідома спроба ін'єкції в рядковий параметр пошуку
st, body = call("GET", "/api/books?title=' OR 1=1 --")
check("спроба SQL-ін'єкції в ?title -> 200 і ПОРОЖНІЙ результат (не всі книги)",
      st == 200 and isinstance(body, list) and len(body) == 0,
      f"status={st} count={len(body) if isinstance(body, list) else '?'}")

st, body = call("GET", "/api/books?title=Кобзар")
check("нормальний пошук за кириличною назвою працює",
      st == 200 and isinstance(body, list) and len(body) == 1,
      f"status={st} count={len(body) if isinstance(body, list) else '?'}")

# =============================================================================
section("9. Оплата пені: повторний запит не дублює платіж, перевищення -> 409")
# =============================================================================
# Пеню видачі №3 seed-скрипт уже оплатив повністю (ключ 'seed-fine-1'),
# тому тут перевіряємо саме ПОВТОР (replay) наявного платежу: шлях
# ідемпотентності має спрацювати РАНІШЕ за бізнес-перевірку «вже оплачено».
st, seeded = call("GET", "/api/loans/3/fine-payments")
check("GET /api/loans/3/fine-payments -> 200 (seed створив платіж)",
      st == 200 and isinstance(seeded, list) and len(seeded) == 1, f"status={st} body={seeded}")
seed_payment_id = seeded[0]["id"] if isinstance(seeded, list) and seeded else None
count_before = len(seeded) if isinstance(seeded, list) else -1

st, replay = call("POST", "/api/loans/3/fine-payments",
                  {"amount": 64.75, "method": "Card", "idempotencyKey": "seed-fine-1"})
check("повтор із наявним idempotencyKey -> повертається ТОЙ САМИЙ платіж",
      isinstance(replay, dict) and replay.get("id") == seed_payment_id,
      f"status={st} expected id={seed_payment_id} got={replay.get('id') if isinstance(replay, dict) else replay}")

st, after = call("GET", "/api/loans/3/fine-payments")
check("другий платіж НЕ створено",
      isinstance(after, list) and len(after) == count_before,
      f"{count_before} -> {len(after) if isinstance(after, list) else '?'}")

st, body = call("POST", "/api/loans/3/fine-payments", {"amount": 999999, "method": "Cash"})
check("повна оплата вже внесена -> 409", st == 409, f"status={st}")

st, body = call("POST", "/api/loans/3/fine-payments", {"amount": -5, "method": "Cash"})
check("відʼємна сума -> 400 (DataAnnotations)", st == 400, f"status={st}")

st, body = call("POST", "/api/loans/3/fine-payments", {"amount": 10, "method": "Bitcoin"})
check("невідомий спосіб оплати -> 400 (RegularExpression)", st == 400, f"status={st}")

# =============================================================================
section("10. Звіт із денормалізованого представлення + прострочені видачі")
# =============================================================================
st, summary = call("GET", "/api/readers/3/loan-summary")
check("GET /api/readers/3/loan-summary -> 200 (процедура usp_GetReaderLoanSummary)",
      st == 200 and isinstance(summary, list) and summary, f"status={st}")
check("звіт містить агрегати (TitlesCount, DailyChargeTotal)",
      st == 200 and summary and "titlesCount" in summary[0] and "dailyChargeTotal" in summary[0],
      f"row={summary[0] if isinstance(summary, list) and summary else '?'}")

st, body = call("GET", "/api/readers/999999/loan-summary")
check("звіт для неіснуючого читача -> 404 (THROW 50001 з процедури)", st == 404, f"status={st}")

st, overdue = call("GET", "/api/loans/overdue")
check("GET /api/loans/overdue -> 200 (фільтрований індекс IX_Loans_Overdue)",
      st == 200 and isinstance(overdue, list), f"status={st}")
check("прострочена видача №2 присутня у списку",
      isinstance(overdue, list) and any(o["id"] == 2 for o in overdue),
      f"ids={[o['id'] for o in overdue] if isinstance(overdue, list) else '?'}")

# =============================================================================
section("10b. НОВА оплата пені: повернення простроченої видачі -> пеня -> оплата")
# =============================================================================
# Повертаємо прострочену видачу №2: процедура нарахує пеню за прострочені дні
# за ЗНІМКОМ тарифу з позицій. Робимо це ПІСЛЯ перевірки списку прострочених.
st, ret2 = call("POST", "/api/loans/2/return", {})
check("повернення простроченої видачі №2 -> 200", st == 200, f"status={st} body={ret2}")
fine2 = float(ret2.get("fineAmount", 0)) if isinstance(ret2, dict) else 0
check("процедура нарахувала пеню > 0 за прострочення", fine2 > 0, f"fineAmount={fine2}")

st, pay_before = call("GET", "/api/loans/2/fine-payments")
n_before = len(pay_before) if isinstance(pay_before, list) else -1

new_key = f"autotest-key-{SUFFIX}"
part = round(fine2 / 2, 2)
st1, q1 = call("POST", "/api/loans/2/fine-payments",
               {"amount": part, "method": "Card", "idempotencyKey": new_key})
check("часткова оплата пені -> 201 Created", st1 == 201, f"status={st1} body={q1}")

st2, q2 = call("POST", "/api/loans/2/fine-payments",
               {"amount": part, "method": "Card", "idempotencyKey": new_key})
check("ПОВТОР того самого запиту -> той самий платіж (ідемпотентність)",
      isinstance(q1, dict) and isinstance(q2, dict) and q1.get("id") == q2.get("id"),
      f"id1={q1.get('id') if isinstance(q1, dict) else q1} id2={q2.get('id') if isinstance(q2, dict) else q2}")

st, pay_after = call("GET", "/api/loans/2/fine-payments")
check("створено РІВНО один новий платіж",
      isinstance(pay_after, list) and len(pay_after) == n_before + 1,
      f"{n_before} -> {len(pay_after) if isinstance(pay_after, list) else '?'}")

st, body = call("POST", "/api/loans/2/fine-payments", {"amount": fine2, "method": "Cash"})
check("оплата понад залишок до сплати -> 409", st == 409, f"status={st}")

# =============================================================================
section("11. Заблокований читач не може оформити видачу")
# =============================================================================
st, _ = call("PUT", "/api/readers/4/blocked?value=true")
check("PUT /api/readers/4/blocked?value=true -> 204", st == 204, f"status={st}")

st, body = call("POST", "/api/loans", {
    "readerId": 4, "dueDate": "2026-12-31", "pickupBranch": "Центральна філія",
    "items": [{"bookId": 1, "units": 1}],
})
check("видача для заблокованого читача -> 409", st == 409, f"status={st}")

st, _ = call("PUT", "/api/readers/4/blocked?value=false")
check("розблокування -> 204", st == 204, f"status={st}")

# =============================================================================
section("12. Створення читача: 201 + конфлікт унікальності 409")
# =============================================================================
st, r = call("POST", "/api/readers", {
    "cardNumber": f"AT-{SUFFIX}",
    "email": f"autotest{SUFFIX}@example.com",
    "fullName": "Автотест Читачівський",
    "phone": "+380509998877",
})
check("POST /api/readers -> 201 Created", st == 201, f"status={st} body={r}")

st, body = call("POST", "/api/readers", {
    "cardNumber": f"AT-{SUFFIX}",
    "email": f"autotest{SUFFIX}@example.com",
    "fullName": "Дублікат",
})
check("повторне створення з тим самим email -> 409", st == 409, f"status={st}")

# =============================================================================
section("13. Swagger / OpenAPI доступний")
# =============================================================================
st, body = call("GET", "/swagger/v1/swagger.json")
check("GET /swagger/v1/swagger.json -> 200", st == 200, f"status={st}")
check("опис містить усі три групи ендпоінтів",
      isinstance(body, dict) and all(
          any(p.startswith(pref) for p in body.get("paths", {}))
          for pref in ("/api/loans", "/api/readers", "/api/books")),
      f"paths={list(body.get('paths', {}).keys()) if isinstance(body, dict) else '?'}")

# =============================================================================
print()
print("=" * 70)
print(f" РЕЗУЛЬТАТ: {len(passed)} пройдено, {len(failed)} провалено")
print("=" * 70)
if failed:
    print("\nПровалені перевірки:")
    for f in failed:
        print(f"  - {f}")
    sys.exit(1)
print("\nУсі критерії приймання Лабораторної роботи № 02 виконано.")
sys.exit(0)

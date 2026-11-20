/* =============================================================================
   ПРОЄКТ №3 — LibraryReviewsDb  (MongoDB)
   Контекст: «Відгуки та обговорення» (Reviews) — динамічна структура даних,
   де в одній колекції співіснують документи з різними наборами полів.
   -----------------------------------------------------------------------------
   Файл: 01-create-collections.js — створення колекцій, JSON Schema Validation
         та індексів.
   Запуск:  mongosh "mongodb://localhost:27017" 01-create-collections.js
   -----------------------------------------------------------------------------
   Моделювання зв'язків:
     embedding   — reviews.author, reviews.comments[], readingLists.items[]
     referencing — reviews.discussionId -> discussions._id
     гібрид      — reviews: кілька останніх коментарів ВБУДОВАНО, а довга
                   дискусія винесена в окрему колекцію ЗА ПОСИЛАННЯМ
   Посилання на інші контексти — лише за ідентифікатором, без FK:
     reviews.bookId      -> Books.Id   у LibraryCatalogDb (Проєкт №2)
     reviews.reader.id   -> Readers.Id у LibraryLendingDb (Проєкт №1)
   ============================================================================= */

const db = db.getSiblingDB("LibraryReviewsDb");

print("=== LibraryReviewsDb: створення колекцій ===");

/* ---------------------------------------------------------------------------
   Ідемпотентність: прибираємо колекції, якщо скрипт запускають повторно.
   (seed-скрипт 02-seed.js ідемпотентний сам по собі — через upsert.)
   --------------------------------------------------------------------------- */
["reviews", "discussions", "readingLists", "readerProfiles"].forEach((name) => {
    if (db.getCollectionNames().includes(name)) {
        db.getCollection(name).drop();
        print(`  drop  ${name}`);
    }
});

/* =============================================================================
   1. reviews — відгуки на книги.
      Головна колекція, що поєднує ВСІ прийоми моделювання.
      JSON Schema Validation контролює лише ОБОВ'ЯЗКОВЕ ядро документа
      (bookId, rating, author) — решта полів вільна, що й дає гнучку схему.
   ============================================================================= */
db.createCollection("reviews", {
    validator: {
        $jsonSchema: {
            bsonType: "object",
            required: ["bookId", "rating", "author", "createdAt"],
            properties: {
                bookId: {
                    bsonType: ["long", "int"],
                    description: "логічне посилання на Books.Id у LibraryCatalogDb (без FK)"
                },
                bookTitle: {
                    bsonType: "string",
                    description: "денормалізовано з Каталогу для читання без міжсервісного виклику"
                },
                rating: {
                    bsonType: "int",
                    minimum: 1,
                    maximum: 5,
                    description: "оцінка від 1 до 5"
                },
                author: {
                    bsonType: "object",
                    required: ["displayName"],
                    properties: {
                        readerId:    { bsonType: ["long", "int", "null"] },
                        displayName: { bsonType: "string" }
                    },
                    description: "ВБУДОВАНИЙ об'єкт — автор відгуку"
                },
                comments: {
                    bsonType: "array",
                    description: "ВБУДОВАНИЙ масив — кілька останніх коментарів",
                    items: {
                        bsonType: "object",
                        required: ["author", "text"],
                        properties: {
                            author:    { bsonType: "string" },
                            text:      { bsonType: "string" },
                            createdAt: { bsonType: "date" },
                            likes:     { bsonType: "int" }
                        }
                    }
                },
                discussionId: {
                    bsonType: ["objectId", "null"],
                    description: "ПОСИЛАННЯ на колекцію discussions (необмежено довга ветка)"
                },
                createdAt: { bsonType: "date" }
            }
        }
    },
    validationLevel: "moderate",   // перевіряти нові та оновлювані документи
    validationAction: "error"
});
print("  create  reviews   (+ JSON Schema Validation)");

/* індекс під запит: «усі відгуки на книгу, найновіші спочатку» */
db.reviews.createIndex({ bookId: 1, createdAt: -1 }, { name: "ix_reviews_book_created" });
/* індекс під запит: «середній рейтинг і розподіл оцінок книги» (aggregation) */
db.reviews.createIndex({ bookId: 1, rating: -1 }, { name: "ix_reviews_book_rating" });
/* індекс під запит: «усі відгуки конкретного читача» — розріджений,
   бо readerId є не в усіх документах (анонімні відгуки) */
db.reviews.createIndex({ "author.readerId": 1 }, { name: "ix_reviews_reader", sparse: true });
/* текстовий індекс під запит: «повнотекстовий пошук по відгуках» */
db.reviews.createIndex({ text: "text", bookTitle: "text" }, { name: "ix_reviews_fulltext", default_language: "russian" });
/* частковий індекс під запит: «відгуки з фото» (поле є лише в частини документів) */
db.reviews.createIndex({ createdAt: -1 },
    { name: "ix_reviews_with_photos", partialFilterExpression: { photos: { $exists: true } } });
print("  index   reviews x5");

/* =============================================================================
   2. discussions — довгі обговорення книг.
      Винесені в ОКРЕМУ колекцію саме тому, що кількість повідомлень
      не обмежена: вбудовувати тисячі повідомлень у відгук не можна
      (ліміт документа 16 МБ + постійне перезаписування).
   ============================================================================= */
db.createCollection("discussions", {
    validator: {
        $jsonSchema: {
            bsonType: "object",
            required: ["bookId", "topic", "createdAt"],
            properties: {
                bookId:    { bsonType: ["long", "int"] },
                bookTitle: { bsonType: "string" },
                topic:     { bsonType: "string" },
                isClosed:  { bsonType: "bool" },
                messages: {
                    bsonType: "array",
                    items: {
                        bsonType: "object",
                        required: ["author", "text"],
                        properties: {
                            author:    { bsonType: "string" },
                            text:      { bsonType: "string" },
                            postedAt:  { bsonType: "date" },
                            replyTo:   { bsonType: ["int", "null"] }
                        }
                    }
                },
                createdAt: { bsonType: "date" }
            }
        }
    },
    validationLevel: "moderate",
    validationAction: "error"
});
print("  create  discussions   (+ JSON Schema Validation)");

/* індекс під запит: «відкриті обговорення книги» */
db.discussions.createIndex({ bookId: 1, isClosed: 1 }, { name: "ix_discussions_book_open" });
/* індекс під запит: «останні активні обговорення» */
db.discussions.createIndex({ lastActivityAt: -1 }, { name: "ix_discussions_activity", sparse: true });
print("  index   discussions x2");

/* =============================================================================
   3. readingLists — підбірки книг читачів.
      Класичний випадок ВБУДОВУВАННЯ: позиції підбірки завжди читаються
      разом із самою підбіркою і їх небагато.
   ============================================================================= */
db.createCollection("readingLists", {
    validator: {
        $jsonSchema: {
            bsonType: "object",
            required: ["title", "owner", "createdAt"],
            properties: {
                title:      { bsonType: "string" },
                isPublic:   { bsonType: "bool" },
                owner: {
                    bsonType: "object",
                    required: ["displayName"],
                    properties: {
                        readerId:    { bsonType: ["long", "int", "null"] },
                        displayName: { bsonType: "string" }
                    }
                },
                items: {
                    bsonType: "array",
                    description: "ВБУДОВАНИЙ масив позицій із денормалізованою назвою книги",
                    items: {
                        bsonType: "object",
                        required: ["bookId"],
                        properties: {
                            bookId:    { bsonType: ["long", "int"] },
                            bookTitle: { bsonType: "string" },
                            note:      { bsonType: "string" },
                            addedAt:   { bsonType: "date" },
                            status:    { bsonType: "string", enum: ["planned", "reading", "finished", "dropped"] }
                        }
                    }
                },
                createdAt: { bsonType: "date" }
            }
        }
    },
    validationLevel: "moderate",
    validationAction: "error"
});
print("  create  readingLists   (+ JSON Schema Validation)");

/* індекс під запит: «публічні підбірки, у яких є конкретна книга» —
   багатоключовий індекс по вбудованому масиву */
db.readingLists.createIndex({ "items.bookId": 1, isPublic: 1 }, { name: "ix_lists_item_book" });
/* індекс під запит: «підбірки читача» */
db.readingLists.createIndex({ "owner.readerId": 1 }, { name: "ix_lists_owner", sparse: true });
print("  index   readingLists x2");

/* =============================================================================
   4. readerProfiles — профілі читачів у контексті Відгуків.
      ДЕМОНСТРАЦІЯ ГНУЧКОЇ СХЕМИ: валідатор вимагає лише readerId
      і displayName, тому документи можуть мати будь-які додаткові поля
      (badges, preferences, socialLinks, stats) — див. 02-seed.js.
   ============================================================================= */
db.createCollection("readerProfiles", {
    validator: {
        $jsonSchema: {
            bsonType: "object",
            required: ["readerId", "displayName"],
            properties: {
                readerId:    { bsonType: ["long", "int"] },
                displayName: { bsonType: "string" }
            }
        }
    },
    validationLevel: "moderate",
    validationAction: "error"
});
print("  create  readerProfiles   (+ JSON Schema Validation, гнучка схема)");

db.readerProfiles.createIndex({ readerId: 1 }, { name: "ix_profiles_reader", unique: true });
print("  index   readerProfiles x1");

print("");
print("=== Готово. Колекції: " + db.getCollectionNames().join(", ") + " ===");
print("Далі: mongosh \"mongodb://localhost:27017\" 02-seed.js");

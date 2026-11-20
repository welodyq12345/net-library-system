/* =============================================================================
   ПРОЄКТ №3 — LibraryReviewsDb : тестові документи
   -----------------------------------------------------------------------------
   Запуск:  mongosh "mongodb://localhost:27017" 02-seed.js
   ІДЕМПОТЕНТНІСТЬ: усі вставки через replaceOne({..}, {..}, {upsert:true})
   з детермінованим _id, тому повторний запуск НЕ створює дублікатів.
   -----------------------------------------------------------------------------
   Документи свідомо мають РІЗНІ набори полів в одній колекції:
     reviews #1 — повний «гібрид»: вбудований автор + коментарі + посилання
     reviews #2 — з масивом photos і tags (у #1 їх немає)
     reviews #3 — анонімний: немає author.readerId, немає comments
     reviews #4 — зі вкладеним об'єктом ratings (детальні оцінки) замість тексту
   ============================================================================= */

const db = db.getSiblingDB("LibraryReviewsDb");

/* детерміновані ObjectId — щоб посилання між колекціями були стабільними */
const DISC_KOBZAR   = ObjectId("66f0c30000000000000000a1");
const DISC_INTERNAT = ObjectId("66f0c30000000000000000a2");

print("=== LibraryReviewsDb: наповнення тестовими даними ===");

/* =============================================================================
   discussions — спочатку, бо на них посилаються reviews
   ============================================================================= */
db.discussions.replaceOne(
    { _id: DISC_KOBZAR },
    {
        _id: DISC_KOBZAR,
        bookId: NumberLong("1"),
        bookTitle: "Кобзар",                      // денормалізовано з Каталогу
        topic: "Чи варто читати «Кобзар» у школі?",
        isClosed: false,
        createdAt: new Date("2026-09-10T09:00:00Z"),
        lastActivityAt: new Date("2026-11-18T14:30:00Z"),
        messages: [                               // вбудований масив повідомлень
            { author: { displayName: "Олена" },  text: "Класика, яку варто перечитувати дорослим.", postedAt: new Date("2026-09-10T09:05:00Z"), replyTo: null },
            { author: { displayName: "Андрій" }, text: "У школі складно, але з хорошим учителем — так.", postedAt: new Date("2026-09-11T10:20:00Z"), replyTo: 0 },
            { author: { displayName: "Марія" },  text: "Погоджуюсь, контекст важливіший за заучування.", postedAt: new Date("2026-11-18T14:30:00Z"), replyTo: 1 }
        ]
    },
    { upsert: true }
);

db.discussions.replaceOne(
    { _id: DISC_INTERNAT },
    {
        _id: DISC_INTERNAT,
        bookId: NumberLong("4"),
        bookTitle: "Інтернат",
        topic: "Мова війни в романі Жадана",
        isClosed: true,                           // закрите обговорення
        closedReason: "Тему вичерпано",           // поле є ЛИШЕ в цьому документі
        createdAt: new Date("2026-10-02T12:00:00Z"),
        lastActivityAt: new Date("2026-10-20T18:00:00Z"),
        messages: [
            { author: { displayName: "Ігор" },   text: "Найсильніше — відсутність пафосу.", postedAt: new Date("2026-10-02T12:10:00Z"), replyTo: null },
            { author: { displayName: "Олена" },  text: "Саме тому він і болить.",            postedAt: new Date("2026-10-20T18:00:00Z"), replyTo: 0 }
        ]
    },
    { upsert: true }
);
print(`  discussions: ${db.discussions.countDocuments()} документів`);

/* =============================================================================
   reviews — ГОЛОВНА демонстрація: embedding + referencing + гібрид + гнучка схема
   ============================================================================= */

/* --- #1 ГІБРИД: вбудований автор, вбудовані коментарі ТА посилання на дискусію --- */
db.reviews.replaceOne(
    { _id: ObjectId("66f0c20000000000000000b1") },
    {
        _id: ObjectId("66f0c20000000000000000b1"),
        bookId: NumberLong("1"),                    // посилання на Каталог (без FK)
        bookTitle: "Кобзар",                      // ДЕНОРМАЛІЗОВАНО для читання
        author: {                                 // ВБУДОВАНИЙ об'єкт
            readerId: NumberLong("1"),              // логічне посилання на Readers.Id
            displayName: "Олена К."
        },
        rating: NumberInt(5),
        text: "Видання з якісними примітками — читати набагато легше.",
        comments: [                               // ВБУДОВАНИЙ масив
            { author: { displayName: "Андрій" }, text: "Згоден, примітки врятували.", createdAt: new Date("2026-09-15T08:00:00Z"), likes: NumberInt(3) },
            { author: { displayName: "Марія" },  text: "А яке саме видання?",          createdAt: new Date("2026-09-16T09:30:00Z"), likes: NumberInt(0) }
        ],
        discussionId: DISC_KOBZAR,                // ПОСИЛАННЯ на окрему колекцію
        createdAt: new Date("2026-09-14T07:45:00Z")
    },
    { upsert: true }
);

/* --- #2 ГНУЧКА СХЕМА: є photos і tags, яких немає в інших документах --- */
db.reviews.replaceOne(
    { _id: ObjectId("66f0c20000000000000000b2") },
    {
        _id: ObjectId("66f0c20000000000000000b2"),
        bookId: NumberLong("4"),
        bookTitle: "Інтернат",
        author: { readerId: NumberLong("2"), displayName: "Андрій Ш." },
        rating: NumberInt(5),
        text: "Прочитав за два вечори. Мова суха і від цього ще сильніша.",
        photos: ["internat-cover.jpg", "internat-page-77.jpg"],   // поле лише тут
        tags: ["війна", "сучасна проза", "мушрід"],                // поле лише тут
        comments: [
            { author: { displayName: "Ігор" }, text: "Варто читати разом із «Ворошиловградом».", createdAt: new Date("2026-11-02T11:00:00Z"), likes: NumberInt(5) }
        ],
        discussionId: DISC_INTERNAT,
        createdAt: new Date("2026-11-01T19:20:00Z")
    },
    { upsert: true }
);

/* --- #3 АНОНІМНИЙ: немає author.readerId, немає comments, немає discussionId --- */
db.reviews.replaceOne(
    { _id: ObjectId("66f0c20000000000000000b3") },
    {
        _id: ObjectId("66f0c20000000000000000b3"),
        bookId: NumberLong("2"),
        bookTitle: "Тигролови",
        author: { displayName: "Анонім" },        // readerId ВІДСУТНІЙ
        rating: NumberInt(4),
        text: "Динамічно, але фінал надто швидкий.",
        createdAt: new Date("2026-10-05T16:00:00Z")
    },
    { upsert: true }
);

/* --- #4 ІНША СТРУКТУРА: замість text — вкладений об'єкт детальних оцінок --- */
db.reviews.replaceOne(
    { _id: ObjectId("66f0c20000000000000000b4") },
    {
        _id: ObjectId("66f0c20000000000000000b4"),
        bookId: NumberLong("5"),
        bookTitle: "Чорний Ворон",
        author: { readerId: NumberLong("3"), displayName: "Марія Б." },
        rating: NumberInt(4),
        ratings: {                                // ВКЛАДЕНИЙ ОБ'ЄКТ — складний агрегат
            plot: NumberInt(5),
            language: NumberInt(4),
            historicalAccuracy: NumberInt(3),
            pacing: NumberInt(4)
        },
        readingContext: {                         // другий рівень вкладеності
            finishedAt: new Date("2026-11-12T00:00:00Z"),
            format: "paper",
            rereading: false
        },
        comments: [],                             // порожній масив — теж варіант
        createdAt: new Date("2026-11-12T20:00:00Z")
    },
    { upsert: true }
);

/* --- #5 мінімальний: лише обов'язкове ядро за JSON Schema --- */
db.reviews.replaceOne(
    { _id: ObjectId("66f0c20000000000000000b5") },
    {
        _id: ObjectId("66f0c20000000000000000b5"),
        bookId: NumberLong("3"),
        bookTitle: "Місто",
        author: { displayName: "Читач без профілю" },
        rating: NumberInt(3),
        createdAt: new Date("2026-11-17T10:00:00Z")
    },
    { upsert: true }
);
print(`  reviews: ${db.reviews.countDocuments()} документів (з різними наборами полів)`);

/* =============================================================================
   readingLists — ВБУДОВУВАННЯ позицій
   ============================================================================= */
db.readingLists.replaceOne(
    { _id: ObjectId("66f0c40000000000000000c1") },
    {
        _id: ObjectId("66f0c40000000000000000c1"),
        title: "Українська класика на зиму",
        isPublic: true,
        owner: { readerId: NumberLong("1"), displayName: "Олена К." },
        createdAt: new Date("2026-09-20T08:00:00Z"),
        items: [                                  // ВБУДОВАНИЙ масив із денормалізацією
            { bookId: NumberLong("1"), bookTitle: "Кобзар",     status: "finished", addedAt: new Date("2026-09-20T08:00:00Z"), note: "Перечитати розділ про Гайдамаків" },
            { bookId: NumberLong("3"), bookTitle: "Місто",      status: "reading",  addedAt: new Date("2026-10-01T08:00:00Z") },
            { bookId: NumberLong("2"), bookTitle: "Тигролови",  status: "planned",  addedAt: new Date("2026-10-15T08:00:00Z") }
        ]
    },
    { upsert: true }
);

db.readingLists.replaceOne(
    { _id: ObjectId("66f0c40000000000000000c2") },
    {
        _id: ObjectId("66f0c40000000000000000c2"),
        title: "Приватний список",
        isPublic: false,
        owner: { displayName: "Анонім" },         // немає readerId
        createdAt: new Date("2026-11-05T08:00:00Z"),
        items: [
            { bookId: NumberLong("4"), bookTitle: "Інтернат", status: "planned", addedAt: new Date("2026-11-05T08:00:00Z") }
        ]
    },
    { upsert: true }
);
print(`  readingLists: ${db.readingLists.countDocuments()} документів`);

/* =============================================================================
   readerProfiles — НАЙЯСКРАВІША демонстрація гнучкої схеми:
   три документи в одній колекції з принципово різними наборами полів.
   ============================================================================= */
db.readerProfiles.replaceOne(
    { readerId: NumberLong("1") },
    {
        readerId: NumberLong("1"),
        displayName: "Олена К.",
        bio: "Читаю українську класику й сучасну поезію.",
        badges: ["top-reviewer", "100-books"],
        stats: { reviewsCount: NumberInt(1), averageRating: 5.0, listsCount: NumberInt(1) },
        preferences: { favouriteGenres: ["Поезія", "Класика"], emailDigest: true, theme: "light" }
    },
    { upsert: true }
);

db.readerProfiles.replaceOne(
    { readerId: NumberLong("2") },
    {
        readerId: NumberLong("2"),
        displayName: "Андрій Ш.",
        socialLinks: { telegram: "@andrii_reads", goodreads: "andrii-sh" },   // поля немає в інших
        stats: { reviewsCount: NumberInt(1), averageRating: 5.0 }
    },
    { upsert: true }
);

db.readerProfiles.replaceOne(
    { readerId: NumberLong("3") },
    {
        readerId: NumberLong("3"),
        displayName: "Марія Б."                   // ЛИШЕ обов'язкові поля
    },
    { upsert: true }
);
print(`  readerProfiles: ${db.readerProfiles.countDocuments()} документів`);

print("");
print("=== Готово. Повторний запуск цього скрипта дублікатів не створює. ===");
print("Далі: mongosh \"mongodb://localhost:27017\" 03-queries.js");

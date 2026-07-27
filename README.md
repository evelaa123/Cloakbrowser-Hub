<div align="center">

<img src="dotnet/src/CloakHub.App/Assets/app-icon.png" width="120" alt="CloakBrowser Hub" />

# CloakBrowser Hub

**Менеджер анти-детект браузера — профили, отпечатки, папки, прокси и автоматизация.**

Построен на [CloakBrowser](https://www.npmjs.com/package/cloakbrowser). .NET 8 + Avalonia.
Windows, Linux и macOS из одной кодовой базы.

[Возможности](#возможности) · [Установка](#установка) · [Сборка](#сборка-из-исходников) · [Автоматизация](#автоматизация) · [Устройство](#устройство)

</div>

---

## Что это

Каждый профиль здесь — отдельная личность: свой отпечаток, своя банка cookies, свой
прокси, свой каталог на диске. Между профилями не разделяется ничего, и в этом весь
смысл: два профиля с общей характеристикой — это два профиля, которые сайт свяжет с
одним человеком.

Hub — это менеджер вокруг этой идеи. Он хранит профили, генерирует внутренне
непротиворечивые отпечатки, группирует их по папкам, запускает сессии и отдаёт
скриптам REST API для управления всем этим без единого клика.

**Приложение готово к работе.** Все разделы реализованы, заглушек не осталось.
Один самодостаточный файл — без .NET runtime, без Node, без установщика.

---

## Возможности

| Раздел | Что умеет |
|---|---|
| **Профили** | Поиск, сортировка, дублирование, удаление, живые счётчики |
| **Папки** | Создание, переименование по месту, удаление, перенос профилей |
| **Редактор профиля** | 8 вкладок: General, Fingerprint, Proxy, Cookies, Locale, Behaviour, Startup, Advanced |
| **Отпечатки** | Согласованные наборы под каждую ОС, перегенерация в один клик |
| **Запуск браузера** | Изоляция данных, лимиты сессий по тарифу, выделение CDP-порта |
| **Прокси** | HTTP/SOCKS с авторизацией, назначение на профиль, проверка с выводом внешнего IP |
| **Cookies** | Импорт и экспорт прямо из редактора профиля |
| **Импорт профилей** | Автопоиск Chromium и Firefox, выбор папки, архивы `.zip`, `.tar`, `.tar.gz`, `.tgz` |
| **Лицензия** | Активация, обновление, тариф и места, маскированный ключ, офлайн-режим |
| **Загрузка бинарников** | Манифест релизов, проверка подписи Ed25519, привязка версии, контроль хеша |
| **Automation API** | Локальный REST для скриптов: Puppeteer, Playwright, Selenium |
| **Хранилище** | Атомарная запись, карантин повреждённых файлов, миграция схемы 1→4 |

---

## Установка

Самодостаточные сборки. Скачать на [странице релизов](../../releases).

| Платформа | Файл | Размер |
|---|---|---|
| Windows x64 | `CloakBrowserHub-v1.0.0-win-x64.zip` | 40 МБ |
| Linux x64 | `CloakBrowserHub-v1.0.0-linux-x64.tar.gz` | 37 МБ |

**Windows** — распаковать, запустить `CloakBrowserHub.exe`. SmartScreen предупредит о
неизвестном издателе, потому что бинарник не подписан: *Подробнее → Выполнить в любом случае*.

**Linux** — нужна графическая сессия (X11 или Wayland):

```bash
tar -xzf CloakBrowserHub-v1.0.0-linux-x64.tar.gz
chmod +x CloakBrowserHub
./CloakBrowserHub
```

**macOS** — код собирается под macOS, и конвейер иконок выдаёт корректный `.icns`, но
готовый бинарник пока не публикуется: неподписанный и не прошедший нотаризацию `.app`
Gatekeeper отклоняет так, что это выглядит как битая загрузка. Соберите сами — см. ниже.

---

## Как это работает

### Согласованные отпечатки

Отпечаток убедителен только тогда, когда его части встречаются вместе в реальном мире.
Машина, заявляющая macOS с рендерером `ANGLE (NVIDIA, ... D3D11)`, описывает компьютер,
которого не может существовать, и одно это противоречие опознаёт вас *сильнее*, чем
честные значения.

Поэтому пулы значений разбиты по платформам и никогда не смешиваются:

- **Вендор и рендерер GPU хранятся парами** — «Apple Inc.» физически не может выпасть
  вместе с рендерером Radeon.
- **Разрешения экрана свои для каждой ОС** — Apple никогда не выпускала панель 1366×768.
- **Локаль и таймзона предлагаются вместе** — `de-DE` в `Asia/Tokyo` проверяется одной
  строкой JavaScript.
- **`deviceMemory` только степени двойки**, потому что спецификация API допускает
  ровно этот набор значений.

Распределения намеренно неравномерны. 1920×1080 встречается в пуле Windows трижды,
потому что оно действительно самое частое. Равномерная выборка из набора правдоподобных
значений даёт популяцию, которая сама по себе неправдоподобна: профиль с разрешением
«одно из девяти» выделяется больше, а не меньше.

### Папки

Группировка в стиле Dolphin Anty: боковая панель с живыми счётчиками, переименование
по Enter, контекстное меню, подменю **Move to** на каждой строке.

**Удаление папки никогда не удаляет профили внутри** — они переезжают в корень.
Удаление контейнера в файловом менеджере забирает содержимое, но профиль — это
проделанная работа, отлежавшаяся личность с cookies и историей, и потерять несколько
таких из-за одного промаха по ярлыку группировки недопустимо.

### Хранилище, которое не теряет работу

- **Атомарная запись** — сначала во временный файл, затем переименование. Падение
  посреди сохранения оставляет прежний файл целым, а не обрезанным.
- **Повреждённые файлы отправляются в карантин, а не перезаписываются.** Если
  `profiles.json` не разбирается, он отодвигается в сторону, а приложение открывается
  пустым с сообщением, где искать файл. Пустой список неотличим от «приложение
  выбросило вашу работу», поэтому оно говорит, куда делись байты.
- **Один нечитаемый профиль не прячет остальные** — плохая запись пропускается с
  сообщением, прочие загружаются.
- **Миграция с проверкой версии.** Заполнение поля выполняется только для профилей
  ниже той версии, где поле появилось, поэтому осознанно очищенное значение
  не воскресает.

### Значки с номерами

Каждая запущенная сессия получает пронумерованный значок, так что двенадцать открытых
окон остаются различимыми: настоящий `.ico` на Windows, `.icns` на macOS и иконки окон
X11 на Linux.

---

## Автоматизация

Локальный REST API позволяет управлять Hub из скрипта: получить список профилей,
запустить нужный, забрать CDP-эндпоинт, подключить Puppeteer, Playwright или Selenium
и остановить сессию. Именно ради этого класса задач — массовых операций с аккаунтами,
проверок по расписанию, сбора данных под стабильной личностью — анти-детект браузер
обычно и покупают.

Включается в **Settings → Automation**. Токен генерируется автоматически.

| Метод и путь | Действие |
|---|---|
| `GET /health` | Проверка доступности |
| `GET /profiles` | Список профилей |
| `POST /profiles` | Создать профиль |
| `GET /profiles/{id}` | Получить профиль |
| `PATCH /profiles/{id}` | Изменить профиль |
| `DELETE /profiles/{id}` | Удалить профиль |
| `POST /profiles/{id}/start` | Запустить сессию |
| `POST /profiles/{id}/stop` | Остановить сессию |
| `GET /profiles/{id}/endpoint` | Получить CDP-эндпоинт |

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:7317/profiles
```

```js
// Puppeteer
const { wsEndpoint } = await fetch(
  `http://127.0.0.1:7317/profiles/${id}/start`,
  { method: 'POST', headers: { Authorization: `Bearer ${token}` } },
).then(r => r.json());

const browser = await puppeteer.connect({ browserWSEndpoint: wsEndpoint });
```

Ответ `start` содержит `wsEndpoint`, `httpEndpoint`, `port`, `profileId`, `profileName`
и `alreadyRunning`. Повторный вызов на уже запущенном профиле — не ошибка: возвращается
тот же эндпоинт с `alreadyRunning: true`, поэтому ретрай после таймаута клиента
безопасен.

**Как устроена безопасность API.** Эндпоинт выдаёт CDP-ссылки, которые дают полный
контроль над страницей и доступ к cookies, поэтому:

- слушает **только loopback** — настройки хоста намеренно не существует;
- **токен обязателен на каждом запросе** и сравнивается за постоянное время.
  JavaScript открытой страницы может обратиться к `127.0.0.1`, так что «это же
  локально» само по себе не граница;
- **сервер отказывается стартовать включённым без токена**, а не выдумывает его
  молча: файл настроек со словами «enabled, no token» должен быть виден тому,
  кто ему доверяет;
- **заголовок `Access-Control-Allow-Origin` не отправляется никогда**, а preflight
  отклоняется явно, поэтому чужая страница не прочитает ответ.

---

## Сборка из исходников

Нужен только [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/evelaa123/Cloakbrowser-Hub.git
cd Cloakbrowser-Hub/dotnet

dotnet build                # 0 предупреждений — они здесь ошибки
dotnet test                 # 673 теста
dotnet run --project src/CloakHub.App
```

### Сборка одного файла

```bash
# Windows
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o artifacts/win-x64

# Linux
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o artifacts/linux-x64

# macOS (Apple Silicon; для Intel — osx-x64)
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/osx-arm64
```

### Диагностика

`CloakHub.Doctor` печатает то, что сделало бы приложение, ничего не запуская: точные
аргументы браузера для профиля, определение хоста, планирование значков, сетевые проверки.

```bash
dotnet run --project src/CloakHub.Doctor -- --help
```

### Иконки

Весь набор иконок выводится из одного мастер-файла, поэтому размеры не могут разойтись
между собой:

```bash
python3 build/make-icon.py      # нужен Pillow
```

---

## Непрерывная интеграция

В репозитории workflow нет: токен, которым велась разработка, не имеет права
`workflows`, поэтому файл нужно добавить вручную. Ниже готовая конфигурация —
положите её в `.github/workflows/ci.yml`.

```yaml
name: CI

on:
  push:
    branches: [main, genspark_ai_developer]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      # Кеш восстановленных пакетов; ключ по хешу всех .csproj.
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ hashFiles('dotnet/**/*.csproj') }}
          restore-keys: nuget-

      - name: Restore
        run: dotnet restore dotnet/CloakBrowserHub.sln

      # -warnaserror держит планку: в проекте включён TreatWarningsAsErrors,
      # и сборка должна падать на первом же предупреждении, а не копить их.
      - name: Build
        run: dotnet build dotnet/CloakBrowserHub.sln -c Release --no-restore -warnaserror

      - name: Test
        run: dotnet test dotnet/CloakBrowserHub.sln -c Release --no-build --verbosity normal
```

Тестам не нужен дисплей: вся логика лежит в `CloakHub.Core`, у которого нет
зависимости от UI, поэтому `ubuntu-latest` без X11 подходит полностью.

### Сборка релизов по тегу

Отдельный workflow — `.github/workflows/release.yml`. Срабатывает на теги вида `v*`
и прикладывает бинарники к релизу GitHub:

```yaml
name: Release

on:
  push:
    tags: ['v*']

permissions:
  contents: write        # нужно, чтобы создать релиз и залить файлы

jobs:
  publish:
    strategy:
      fail-fast: false   # сборка под Linux не должна отменять сборку под Windows
      matrix:
        include:
          - os: ubuntu-latest
            rid: linux-x64
          - os: windows-latest
            rid: win-x64

    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Publish
        shell: bash
        run: |
          dotnet publish dotnet/src/CloakHub.App/CloakHub.App.csproj \
            -c Release -r ${{ matrix.rid }} --self-contained true \
            -p:PublishSingleFile=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:EnableCompressionInSingleFile=true \
            -p:DebugType=none \
            -o artifacts/${{ matrix.rid }}

      - name: Package
        shell: bash
        run: |
          cd artifacts/${{ matrix.rid }}
          if [ "${{ matrix.rid }}" = "win-x64" ]; then
            7z a ../../CloakBrowserHub-${{ matrix.rid }}.zip .
          else
            chmod +x CloakBrowserHub
            tar -czf ../../CloakBrowserHub-${{ matrix.rid }}.tar.gz .
          fi

      - uses: softprops/action-gh-release@v2
        with:
          files: CloakBrowserHub-${{ matrix.rid }}.*
```

Дополнительных секретов не требуется: `GITHUB_TOKEN` выдаётся автоматически, а
`permissions: contents: write` даёт ему право создать релиз. Подписи кода здесь нет —
если понадобится подписывать Windows-сборку, добавьте шаг с `signtool` и сертификатом
из секретов репозитория.

---

## Устройство

```
dotnet/
├── src/
│   ├── CloakHub.Core/          # Без UI. Вся логика, полностью тестируемая.
│   │   ├── Model/              # Profile, Fingerprint, Defaults (пулы + фабрика)
│   │   ├── Storage/            # ProfileStore, JsonStore, ProfileMigration
│   │   ├── Launch/             # FingerprintArgs, PrivacyArgs, SessionManager
│   │   ├── Import/             # Автопоиск браузеров, архивы, клонирование
│   │   ├── Cookies/            # Разбор, проверка, запись в Chromium DB
│   │   ├── Automation/         # Локальный REST API
│   │   ├── Binaries/           # Манифест релизов, проверка подписи, установка
│   │   ├── Branding/           # Значки сессий (.ico/.icns/X11)
│   │   ├── Licensing/          # Разбор ключей, лимиты сессий
│   │   ├── Network/            # Планирование MAC-адресов
│   │   └── Platform/           # Определение ОС
│   ├── CloakHub.App/           # Avalonia UI — только представления и view models
│   └── CloakHub.Doctor/        # Диагностическая CLI
└── tests/
    ├── CloakHub.Core.Tests/    # 651 тест
    └── CloakHub.App.Tests/     # 22 теста
```

**Все решения принимает Core, UI не принимает ни одного.** В UI-проекте нет логики
отпечатков, знания о формате файлов и построения аргументов. Поэтому диагностическая
CLI выдаёт побайтово те же аргументы запуска, что и приложение — они вызывают один код —
и поэтому правила тестируются без графической сессии.

Две договорённости, которые стоит знать перед контрибьютом:

- **Предупреждения — это ошибки** (`TreatWarningsAsErrors`). В сборке ноль
  предупреждений, и так должно остаться.
- **Компилируемые привязки включены по умолчанию.** Каждое представление объявляет
  `x:DataType`, поэтому привязка к несуществующему свойству — ошибка сборки, а не
  молча пустое поле в рантайме.

### Где лежат данные

| ОС | Путь |
|---|---|
| Windows | `%APPDATA%\CloakBrowserHub\` |
| macOS | `~/Library/Application Support/CloakBrowserHub/` |
| Linux | `~/.config/CloakBrowserHub/` |

`profiles.json` хранит профили и папки, `settings.json` — настройки. Данные браузера
лежат в подкаталоге `profiles/`, его можно перенести в настройках.

---

## История

Проект начинался как приложение на Electron + Preact и переписан на .NET 8 + Avalonia.
Переход завершён: один тулчейн, один язык и самодостаточный бинарник вместо
поставляемого в комплекте Chromium.

Electron-реализация целиком сохранена в ветке
[`electron-legacy`](../../tree/electron-legacy) — она остаётся рабочей и доступной
для справки.

---

## Что стоит понимать

- **MAC-адрес и имя устройства не влияют на отпечаток браузера.** Ни один веб-API их
  не отдаёт — ни `navigator`, ни WebRTC, ни WebGL. Они меняют то, что видит *локальная
  сеть*. Они смоделированы, потому что так делают другие инструменты и пользователи
  закономерно об этом спрашивают, и интерфейс прямо говорит об ограничении, а не
  намекает на несуществующую пользу.
- **Пошумовые настройки пока сводятся к одному флагу.** Бинарник CloakBrowser
  предоставляет единственный переключатель `--fingerprint-noise`, покрывающий canvas,
  WebGL, audio и client rects разом. Четыре значения хранятся раздельно, чтобы
  интерфейс уже давал привычный контроль, а будущему бинарнику не потребовалась
  миграция — но сегодня запрос шума на любой поверхности включает его для всех.
- **Неопределяемых отпечатков не бывает.** Достаточно упорный сайт способен заметить
  сам факт подмены значений. Задача здесь — не дать связать *ваши профили между собой*,
  и это свойство и достижимее, и полезнее.

---

## Лицензия

MIT.

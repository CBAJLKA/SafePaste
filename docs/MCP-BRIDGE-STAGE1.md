# План первого этапа моста SafePaste (MCP)

Исторический план: после решения пользователя мост встроен в основной `SafePaste.exe`.
С этапа 6 мост работает внутри SafePaste в трее, а `--mcp` стал разъёмом к нему; параметры
`--roots`, `--page-chars` и `SAFEPASTE_ROOTS` ниже больше не действуют, см. [MCP-BRIDGE.md](MCP-BRIDGE.md).
Актуальные команды, инструменты и статус фаз описаны в [MCP-BRIDGE.md](MCP-BRIDGE.md).
Упоминания отдельного `SafePaste.Mcp.exe` ниже относятся к исходной схеме.

Статус: реализация этапа 1 выполнена 2026-09-23. `build.cmd check` и
`pwsh tools\BridgeSmoke.ps1` проходят на вымышленных данных; установщик проверен во
временных каталогах в PowerShell 7 и 5.1. Реальная сессия Codex и установка в настоящую
приватную папку требуют отдельного согласия пользователя. Исходники прототипа удалены,
но старый `bin\McpSpike.exe` остался: автоматическая проверка отклонила удаление файла.

План для агента, который доделает этап 1 из [MCP-BRIDGE.md](MCP-BRIDGE.md). Сначала прочитать
MCP-BRIDGE.md и [ARCHITECTURE.md](ARCHITECTURE.md) целиком, здесь только то, что нужно сделать,
и решения, принятые после проекта.

## Решения пользователя

- Воркер этапа 1: PowerShell и файлы. Локальной модели в этом этапе нет.
- Метки общие для всех сессий, клиентов и вставки из буфера (один файл `labels.dat`).
- Основной клиент Codex (CLI 0.144.4 на этой машине). Claude поддержать конфигами, проверять
  в первую очередь Codex.
- Полный PowerShell пользователь не решил. Полный профиль, `sp_edit`, `sp_write` и окна
  подтверждения относятся к этапу 2 и сейчас не делаются.

## Что уже есть

- `src/SafePaste/Detecting/ControlMode.cs`: добавлен `ControlMode.Bridge` и `HiddenInBridge`.
  Включённые по умолчанию находки скрываются; из выключенных остаются `IP`, `IPV6`, `SID`, `GUID`,
  `PATH` (маски, `127.0.0.1`, встроенные SID, нулевой GUID, путь целиком), остальные догадки
  (`HOST`, `SERIAL`, `PERSON`, `FQDN`) скрываются. В интерфейс режим не попадает: `ControlModes.Parse`
  знает три режима, меню и окно проверки перебирают индексы 0..2. Тестов на режим ещё нет.
  `build.cmd test` после правки: 318 проверок, провалов нет.
- Прототип `tools/McpSpike.cs` и прогон `pwsh tools\McpSpike.ps1`: рабочие цикл JSON-RPC,
  изоляция stdio (`StdIo.Isolate`), подстановка по токенам (`Rehydrator`), ограниченный runspace
  (`PowerShellWorker`), `LeakGuard`. Взять оттуда код и после этапа удалить оба файла
  и `bin\McpSpike.exe`.
- Черновик скилла `integrations/skills/safepaste-bridge/` (SKILL.md и agents/openai.yaml).
- В git нет ни одного коммита, всё неотслеживаемое. Не коммитить без просьбы пользователя.

## Ограничения

- Сборка встроенным `csc` через `build.cmd`, поэтому C# 5: без `$"..."`, `nameof`, `?.`, `out var`,
  инициализаторов автосвойств, кортежей, локальных функций. Только сборки Framework, NuGet нет.
- `build.cmd` строго ASCII. Исходники и доки в UTF-8 без BOM, переводы строк LF, комментарии
  и тексты по-русски, как в соседнем коде.
- В текстах интерфейса и документации нет длинных тире, точек-разделителей и двойных пробелов
  (`build.cmd uitest` проверяет строковые литералы UI). Спецсимволы в C#-литералах держать
  экранированными (`\u2014`) и после записи файла проверить, что они не превратились в символы.
- Python на машине нет; есть pwsh 7, Windows PowerShell 5.1 и Git Bash.
- Гарантии README: ничего не уходит в сеть, исходный текст не пишется на диск и в журналы,
  пароли и ключи на диск не сохраняются, при любой ошибке отказ.
- Если антивирус блокирует свежий exe: собрать библиотеку и вызвать из `powershell.exe`,
  как в разделе «Отладка без запуска exe» в ARCHITECTURE.md.
- Реальные `~/.codex`, `~/.claude` и `%LOCALAPPDATA%\SafePaste` не трогать: проверки во временных
  каталогах (`--data-dir`, `-Workspace`, `-SkillsHome`).

## Проверенные факты

Прототип на этой машине:

- `System.Management.Automation.dll` (PowerShell 5.1) лежит в
  `%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\`
  и подключается через `/r:`.
- Runspace в `NoLanguage` не принимает `AddScript` («Синтаксис не поддерживается этим пространством
  выполнения»). Команда превращается в конвейер через `ScriptBlock.Create(text).GetPowerShell()`,
  который сам отклоняет выражения, переменные (`$env:USERNAME`) и блоки `{ }`.
- Команда с `Visibility = Private` даёт `CommandNotFoundException`. `Resolve-DnsName`
  и `Get-NetIPAddress` после `ImportPSModule` работают.
- `Get-ChildItem -LiteralPath Env:` в ограниченном runspace читается: провайдеры надо закрыть.
- `Out-String` в конце конвейера копит весь вывод, после `Stop()` готовая часть теряется.
- `Parser.ParseInput`: `Get-Item [HOST_1]` даёт токен `Generic` `[HOST_1]`,
  `\\[HOST_1]\[SHARE_2]\docs` один `Generic`, строки дают `StringLiteral` и `StringExpandable`.
- Вывод `cmd.exe` в cp866 декодируется верно при наличии консоли; при запуске из Codex
  не проверено.

Codex CLI 0.144.4:

- `codex features list`: `shell_tool` включён, `unified_exec` выключен, `browser_use`,
  `in_app_browser`, `computer_use`, `hooks` включены. Выключение: `[features] shell_tool = false`
  или `codex --disable shell_tool`.
- `-p <имя>` накладывает `$CODEX_HOME/<имя>.config.toml`, `-C <папка>` задаёт рабочую папку.
- Ключи MCP-сервера в конфиге пользователя: `command`, `args`, `env`, `startup_timeout_sec`,
  `tool_timeout_sec`, `enabled`, `default_tools_approval_mode = "auto"`.
- `.codex/config.toml`, `.codex/hooks.json` и `AGENTS.md` из папки проекта читаются только
  для доверенного проекта.
- Хуки: события как в Claude Code, JSON на stdin. Блок сообщения:
  `{"decision":"block","reason":"..."}`. Отказ инструменту:
  `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"..."}}`.
  Упавший или зависший хук ничего не блокирует.
- В `~/.codex/config.toml` пользователя есть серверы `unityMCP`, `local_qwen`, `blender`,
  `node_repl`: в приватной папке их выключить.
- Не проверено, выяснить на месте: как Codex называет MCP-инструменты в `tool_name` хуков;
  использует ли `instructions` сервера; остаётся ли `apply_patch` при `shell_tool = false`;
  таймаут вызова по умолчанию; выключает ли `[mcp_servers.x] enabled = false` в папке проекта
  сервер из пользовательского конфига.

## Шаги

### 1. Нумерация через интерфейс

`src/SafePaste/Detecting/Replacer.cs`, `src/SafePaste/Storage/Database.cs`.

- Интерфейс `IPlaceholderNumbers` с методами `GetReservedIndex(type, value)`
  и `IsIndexReserved(type, index)`. `SafePasteDatabase` его реализует (методы уже есть).
- `Replacer.Apply(text, detections, IPlaceholderNumbers numbers)` вместо параметра
  `SafePasteDatabase`: старые вызовы компилируются без изменений.
- Перегрузка с `keepLineBreaks`: после метки дописываются переводы строк, которые были внутри
  заменённого значения. Тогда номера строк обезличенного текста совпадают с исходным
  (многострочный ключ не сдвигает строки). Нужно для постраничной выдачи и частичного вывода.
- `ReplacementResult.Applied`: заменённые находки в порядке текста, с `Placeholder`.

### 2. Хранилище меток

`src/SafePaste/Bridge/LabelStore.cs`, namespace `SafePaste.Bridge`, без ссылок на PowerShell
(папка компилируется и в `SafePaste.exe`).

- `Paths.LabelsFile` = `labels.dat` в `Paths.DataDirectory`. JSON
  `{"Labels":[{"Type":"IP","Value":"10.44.7.219","Index":7,"Used":"2026-09-23"}]}` под DPAPI
  (`CurrentUser`), атомарная запись через временный файл и `File.Replace`, как `SafePasteDatabase.Save`.
- В памяти словари по «тип + значение без учёта регистра» и по метке `[TYPE_N]`, занятые номера
  по типам. Секреты (`Locked` и типы `SECRET`, `TOKEN`, `PRIVATE_KEY`) живут только в памяти
  процесса: участвуют в нумерации, в файл не пишутся.
- Именованный мьютекс `Local\SafePaste-Labels-<SID>`, ожидание около 5 с, по таймауту исключение.
- `Apply(text, detections, userDatabase, keepLineBreaks)` целиком под мьютексом: перечитать файл,
  если изменилось время записи (файл главнее для обычных меток, секреты из памяти остаются);
  нумерация составная (закрепление пользователя главнее, затем номер из хранилища; занятым
  считается номер из закреплений и из хранилища, включая секреты); `Replacer.Apply`; записать
  новые метки из `Applied`; обновить `Used`; сохранить, если что-то изменилось.
- При загрузке и перечитывании выбросить записи с `Used` старше 30 дней и записи, которые
  спорят с закреплением пользователя (тот же тип и номер, другое значение).
- `TryResolve(label, out value, out secret)` отмечает `Used`, сохранение отложенное (`Flush`
  под мьютексом в конце вызова инструмента).
- `Hide(value, type)` для `sp_hide`, снимок записей для сканирования и сторожа, счётчик `Version`
  (растёт при любом изменении), описание для `sp_status` (метки и типы, без значений).
- Режим без файла (`--no-persist`, тесты) и `OpenIfExists()` для трея: `null`, если файла нет.

### 3. Общая нумерация со вставкой из буфера

`src/SafePaste/TrayApplication.cs`, `src/SafePaste/Ui/ReviewForm.cs`.

- Если `labels.dat` есть (мостом пользовались), быстрая вставка и `ReviewForm.BuildResult`
  вызывают `store.Apply(..., keepLineBreaks: false)` вместо `Replacer.Apply`: метки во вставке
  совпадают с метками моста и записываются в хранилище. Предпросмотр в `RefreshAll` берёт номера
  из хранилища только на чтение, без записи.
- Без `labels.dat` поведение прежнее (старые тесты это доказывают). Обнаружение в буфере
  не меняется: значения из хранилища для вставки не заучиваются.
- Ошибка чтения `labels.dat` отменяет вставку с понятным сообщением, как `DatabaseException`.
- Зачем: иначе `[IP_1]` из вставки и `[IP_1]` моста значат разные адреса, и команда агента уйдёт
  не на тот узел.

### 4. Обезличивание и сторож

`src/SafePaste/Bridge/Labels.cs`, `Anonymizer.cs`, `LeakGuard.cs`.

- `Labels`: шаблон метки `\[([A-Z][A-Z0-9_]*?)_(\d{1,6})\]`; строки вида `[X_N]` и `[~X_N]`
  в сырых данных получают ещё одну тильду (`[~X_N]`); в тексте агента тильда снимается;
  простая подстановка значений для путей и шаблонов поиска (неизвестная метка или секрет дают
  отказ без значений).
- `Anonymizer.Anonymize(raw)`: экранирование чужих меток; база для сканирования = заученное
  и исключения пользователя плюс значения хранилища как заученные (добавлять прямо в список,
  не через `AddLearned`, чтобы не было квадратичной сложности; кэш по времени `database.dat`
  и `store.Version`); `Detector.Scan(text, scanDb, ControlMode.Bridge, false)`;
  `store.Apply(..., keepLineBreaks: true)`; сторож. База пользователя перечитывается при изменении
  `database.dat`; `DatabaseException` и `DetectionTimeoutException` дают отказ без текста.
- `LeakGuard` по всем записям хранилища, включая секреты в памяти:
  значение от 8 символов найдено где угодно без учёта регистра, даже внутри слова: заменить меткой;
  base64 (UTF-8, UTF-16LE) и hex (верхний и нижний регистр) значений от 4 символов:
  `[ENCODED:TYPE_N]`;
  значение от 8 букв и цифр найдено в тексте, где оставлены только буквы и цифры (с картой
  индексов), и исходный участок содержит разделители и не задевает метку: участок заменить
  на `[SPLIT:TYPE_N]`. В предупреждении только метки.
- Одна точка выхода: всё, что уходит агенту (результаты, тексты ошибок, `sp_status`, исключения
  PowerShell с подставленной командой), проходит `Anonymizer`.

### 5. Сервер MCP

Папка `src/SafePaste.Mcp`, namespace `SafePaste.Mcp`, ссылается на `System.Management.Automation`,
в `SafePaste.exe` не входит.

- `McpProgram.Main`: ключи `--hook prompt|tool` (шаг 9), `--roots "a;b"` или переменная
  `SAFEPASTE_ROOTS`, `--data-dir <папка>` (подмена `Paths.DataDirectory` для тестов),
  `--no-persist`, `--page-chars N` (по умолчанию 12000). Затем `StdIo.Isolate` из прототипа
  и цикл сервера.
- `McpServer`: JSON-RPC 2.0 по строке на сообщение, `JavaScriptSerializer` с
  `MaxJsonLength = int.MaxValue`, UTF-8 без BOM.
  - `initialize`: версии 2025-11-25, 2025-06-18, 2025-03-26, 2024-11-05; отвечать версией клиента,
    если она в списке, иначе 2025-06-18; `capabilities.tools.listChanged = false`;
    `serverInfo.name = "safepaste"`; `instructions` с короткими правилами из скилла.
  - `ping`, `tools/list`, `tools/call`. Уведомления без ответа; `notifications/cancelled`
    останавливает задачу этого запроса, ответ не отправляется.
  - Неизвестный метод: -32601, битый JSON: -32700, ошибка инструмента: результат с `isError: true`.
  - На `initialize` отвечать меньше чем за секунду: runspace и база грузятся лениво.
  - Запросы в пуле потоков, запись в stdout под блокировкой. Сервер работает и на
    `TextReader`/`TextWriter`, чтобы его можно было тестировать без процесса.
- Инструменты (простые JSON Schema: `type: object`, свойства string, integer, boolean, `required`;
  `annotations.readOnlyHint = true` у всех, у `sp_run` ещё `openWorldHint = true`):
  - `sp_status {}`;
  - `sp_list {path?, depth? от 1 до 3, pattern?}`;
  - `sp_read {path, offset? строка с 1, limit? по умолчанию 400, не больше 2000}`;
  - `sp_find {pattern, path?, glob? по умолчанию *, regex? false, case_sensitive? false, max_results? 100}`;
  - `sp_run {command, wait_sec? по умолчанию 30, не больше 120}`;
  - `sp_next {id, stop? false, wait_sec?}`;
  - `sp_hide {value, type? по умолчанию TEXT}` (добавить `TEXT` в `Ui/TypeNames.cs`).

### 6. Ограниченный PowerShell

`src/SafePaste.Mcp/PowerShellWorker.cs`. В этом этапе это единственный профиль.

- Один runspace на процесс, открывается при первом `sp_run`: `InitialSessionState.CreateDefault2()`,
  `ImportPSModule` для Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility,
  Microsoft.PowerShell.Diagnostics, CimCmdlets, DnsClient, NetTCPIP, NetAdapter. После открытия:
  `PSModuleAutoLoadingPreference = None`; все команды (Alias, Function, Filter, Cmdlet) `Private`,
  кроме списка; `Applications` только полные пути разрешённых программ из System32; `Scripts`
  пустой; убрать диски Env, Variable, Function, Alias, HKLM, HKCU, Cert, WSMan, если они есть;
  `LanguageMode = NoLanguage`.
- Разрешённые команды: Get-ChildItem, Get-Item, Get-Content, Test-Path, Resolve-Path, Split-Path,
  Join-Path, Get-Location, Set-Location, Get-Service, Get-Process, Get-HotFix, Get-ComputerInfo,
  Get-TimeZone, Get-Date, Get-EventLog, Get-WinEvent, Get-CimInstance, Resolve-DnsName,
  Get-DnsClientServerAddress, Get-DnsClientCache, Get-NetIPAddress, Get-NetIPConfiguration,
  Get-NetRoute, Get-NetTCPConnection, Get-NetAdapter, Test-NetConnection, Test-Connection,
  Select-Object, Sort-Object, Where-Object, Group-Object, Measure-Object, Select-String,
  Format-List, Format-Table, Out-String, ConvertTo-Json, ConvertTo-Csv, Get-FileHash, Get-Unique,
  Write-Output.
- Разрешённые программы и их аргументы: ping, tracert, pathping, nslookup, getmac, netstat,
  whoami, systeminfo, tasklist, driverquery (любые аргументы); ipconfig (только `/all`,
  `/displaydns`, `/allcompartments`, `/?`); arp (только `-a` или `-g`, можно с адресом
  и `-N адрес`); hostname (без аргументов).
- Запуск: подстановка (шаг 7); `ScriptBlock.Create(text).GetPowerShell()`, на
  `ScriptBlockToPowerShellNotSupportedException` и `ParseException` понятный отказ; проверка
  каждого аргумента каждой команды:
  - строки с `::` или с диском не файловой системы (`env:`, `variable:`, `function:`, `alias:`,
    `hklm:`, `hkcu:`, `cert:`, `wsman:`) отклоняются;
  - если заданы корни, строки, похожие на путь (есть `\`, `/` или `X:`), после разрешения
    от текущей папки должны лежать внутри корней, `Set-Location` тоже;
  - правила программ.

  Затем в конец конвейера `Out-String -Stream -Width 4096`, для всех команд
  `MergeMyResults` ошибок и предупреждений в вывод, `BeginInvoke` с `PSDataCollection` и
  `DataAdded`, который дописывает строки в буфер. Предел 2 000 000 символов: дальше остановка
  и пометка, что вывод обрезан.
- Задачи: `sp_run` ждёт `wait_sec`. Готово: весь вывод обезличивается один раз и делится
  на страницы. Не готово: номер задачи и обезличенные полные строки на этот момент. `sp_next`
  ждёт снова и отдаёт следующие строки или страницу, `stop` вызывает `PowerShell.Stop()`.
  Одновременно идёт один конвейер: второй `sp_run` получает отказ с номером идущей задачи.
  После задачи запомнить текущую папку для файловых инструментов (пока конвейер идёт,
  `SessionStateProxy` не трогать).
- Частичный вывод: каждый раз обезличивать весь сырой вывод с `keepLineBreaks` и отдавать строки
  после уже отданных, до конца задачи только полные строки. Строки совпадают с сырыми, поэтому
  ключ, разрезанный между порциями, не утечёт.

### 7. Подстановка в команды

`src/SafePaste.Mcp/Rehydrator.cs`, основа из прототипа.

- Токены `Parser.ParseInput`. Голое слово (`Generic`) целиком в одинарные кавычки, а если в нём
  есть `$` или обратный апостроф, отказ с советом взять в кавычки. `StringLiteral`: одинарные
  кавычки внутри значения удваиваются, включая U+2018, U+2019, U+201A, U+201B. `StringExpandable`:
  обратный апостроф перед `` ` ``, `$`, `"`, U+201C, U+201D, U+201E. Любой другой токен с меткой
  (переменная, тип, комментарий, here-строка): отказ.
- Неизвестная метка и метка секрета: отказ с понятным текстом, без значений. `[~X_N]` становится
  текстом `[X_N]`. Каждая подставленная метка отмечает `Used`.

### 8. Файловые инструменты

`src/SafePaste.Mcp/FileTools.cs`.

- Корни из `--roots` или `SAFEPASTE_ROOTS`; если не заданы, ограничения нет, и `sp_status` об этом
  пишет. Проверка по полному пути (`Path.GetFullPath`) с учётом разделителя; UNC только внутри корня.
- `sp_list`: сначала папки, потом файлы; строка «папка имя\» или «файл имя размер дата»;
  не больше 500 строк; глубина до 3; весь текст обезличивается целиком.
- `sp_read`: файлы больше 2 МБ отклоняются с подсказкой про `sp_find` и `Select-String`; двоичный
  файл, если в первых 8 КБ есть NUL (кроме UTF-16 с BOM); кодировка по BOM (UTF-8, UTF-16 LE и BE),
  иначе строгий UTF-8, иначе `Encoding.Default` (здесь cp1251); файл обезличивается целиком
  с `keepLineBreaks`; кэш на 4 файла по пути, длине, времени записи и `store.Version`; вывод:
  заголовок (обезличенный путь, всего строк, диапазон) и строки «номер| текст»; строки длиннее
  2000 символов обрезаются, но не посреди метки; если осталось ещё, указать `offset` для продолжения.
- `sp_find`: подстановка в шаблон простая; текст или регулярка с таймаутом 1 с; обход папки
  с фильтром `glob`, пропуск файлов больше 20 МБ и двоичных, остановка на 20 000 файлах, 20 с
  или `max_results`; строки «путь:строка: текст» собираются вместе и обезличиваются одним разом.
- Постраничная выдача: ответ длиннее `--page-chars` получает номер, продолжение через `sp_next`.
  Граница страницы на переводе строки и никогда внутри метки. Номера задач и страниц общие.

### 9. Хуки

`SafePaste.Mcp.exe --hook prompt|tool` читает JSON со stdin, пишет JSON в stdout и выходит с кодом 0;
при внутренней ошибке код 2 и сообщение в stderr.

- `prompt`: поле `prompt`. Начинается с `!raw`: пропустить без вывода. Иначе
  `Detector.Scan(prompt, userDb, settings.Mode, quick: true)`, то есть то, что скрыла бы быстрая
  вставка. Если что-то нашлось: `{"decision":"block","reason":"..."}` с типами и количеством
  (без значений) и советом вставить текст через SafePaste или начать сообщение с `!raw`.
  База не читается: тоже блок с объяснением.
- `tool`: поле `tool_name`. Разрешены инструменты сервера safepaste (шаблон
  `(^|__|[./:])safepaste(__|[./:])`) и служебные: update_plan, request_user_input, TodoWrite,
  AskUserQuestion, Skill, ToolSearch, Agent, Task, EnterPlanMode, ExitPlanMode. Остальное: отказ
  через `hookSpecificOutput` с объяснением, что данные доступны только через `sp_*`. Формат
  `tool_name` у MCP-инструментов проверить в Codex 0.144.4 и поправить шаблон.

### 10. Сборка

`build.cmd`, только ASCII.

- Переменная `SMA` с путём к `System.Management.Automation.dll`, проверка наличия и сообщение.
- `build.cmd` без аргументов собирает `bin\SafePaste.exe` и `bin\SafePaste.Mcp.exe`.
  `SafePaste.exe` не ссылается на SMA и не включает `src\SafePaste.Mcp`.
- `build.cmd mcp`: только мост, `/target:exe /main:SafePaste.Mcp.McpProgram`,
  `/recurse:src\SafePaste\*.cs /recurse:src\SafePaste.Mcp\*.cs /r:<SMA>`.
- `build.cmd test`: добавить `/recurse:src\SafePaste.Mcp\*.cs` и `/r:<SMA>`.
- `build.cmd check`: собрать оба exe и прогнать `test` и `uitest`, ненулевой код при любом провале.

### 11. Установка для Codex и Claude

`integrations/install.ps1`, работает в pwsh и Windows PowerShell 5.1 (кириллица внутри: сохранить
с BOM или обойтись ASCII).

- Параметры: `-Workspace` (обязательный), `-Roots`, `-Codex` (по умолчанию), `-Claude`,
  `-SkillsHome` (по умолчанию `~/.codex/skills` и `~/.claude/skills`), `-WhatIf`.
- Для Codex в папке создаются:
  - `.codex/config.toml`: `[features]` с `shell_tool`, `unified_exec`, `browser_use`,
    `in_app_browser`, `computer_use` = false; `[mcp_servers.safepaste]` с `command` путём к
    `bin\SafePaste.Mcp.exe` литеральной строкой TOML (одинарные кавычки), `args`, `env` с
    `SAFEPASTE_ROOTS`, `startup_timeout_sec = 20`, `tool_timeout_sec = 660`,
    `default_tools_approval_mode = "auto"`; `enabled = false` для `local_qwen`, `node_repl`,
    `unityMCP`, `blender`;
  - `.codex/hooks.json` с `UserPromptSubmit` и `PreToolUse` (таймаут 10);
  - `AGENTS.md` из двух строк;
  - копия `integrations/skills/safepaste-bridge` в `SkillsHome`.
- Для Claude: `.mcp.json` (`timeout` 660000), `.claude/settings.json` (запреты, хуки,
  `allow: ["mcp__safepaste"]`) по образцам из MCP-BRIDGE.md.
- `~/.codex/config.toml` никогда не менять. Проверять на временных `-Workspace` и `-SkillsHome`;
  запуск на настоящих папках только с согласия пользователя.

### 12. Скилл

`integrations/skills/safepaste-bridge/SKILL.md` привести к этапу 1: семь инструментов; правок
файлов пока нет (изменение предложить пользователю текстом с метками); правила ограниченного
профиля; разрешённые программы и их аргументы; значения `[~X]`, `[ENCODED:X]`, `[SPLIT:X]`;
номера для `sp_next`; корни. `agents/openai.yaml` оставить.

### 13. Документация

- `docs/MCP-BRIDGE.md`: статус «этап 1 сделан», решения пользователя вместо раздела «Что нужно
  решить», фактические ключи, лимиты и инструменты, установка через `install.ps1`, убрать ссылки
  на прототип.
- `docs/ARCHITECTURE.md`: строки таблицы модулей для `Bridge/*` и `SafePaste.Mcp/*`, `labels.dat`
  в разделе «База», `ControlMode.Bridge`, новые цели `build.cmd`.
- `README.md`: короткий раздел «Мост для ИИ-агентов» и новые цели сборки.
- Этот файл удалить или пометить выполненным.

### 14. Прототип и дымовая проверка

- Удалить `tools/McpSpike.cs`, `tools/McpSpike.ps1`, `bin\McpSpike.exe`.
- Из прогона прототипа сделать `tools/BridgeSmoke.ps1` (pwsh): запускает
  `bin\SafePaste.Mcp.exe --data-dir <временная папка>`, пишет вымышленные данные во `%TEMP%`,
  вызывает все семь инструментов, проверяет, что ни одно вымышленное значение не попало в ответы
  и что метки подставляются обратно; при провале код 1.

## Тесты

Новый `tests/BridgeTests.cs` как `partial class TestProgram` (в `tests/Tests.cs` сделать класс
`partial` и вызвать `BridgeTests()` из `Main` до `PerformanceTest`). Каталог данных уже подменён
на временный в `Main`.

1. Режим моста: голое `DC01` и `FOC1234ABCD` скрыты; `255.255.255.0`, `127.0.0.1`, `S-1-5-18`,
   нулевой GUID видны; `\\fs01\Share\x` скрыт по частям, путь целиком нет; `C:\Users\ivanov\...`
   скрывает пользователя; пароль скрыт; `ControlModes.Parse("Bridge", ...)` возвращает запасной режим.
2. `Replacer`: `keepLineBreaks` сохраняет число строк для PEM-ключа; порядок `Applied`;
   с `SafePasteDatabase` через интерфейс результат прежний.
3. `LabelStore`: одно значение получает одну метку во всех вызовах; новые значения обходят занятые
   номера и закрепления; второй экземпляр видит метки первого; секретов нет в расшифрованном файле,
   но в процессе их метки стабильны; два экземпляра пишут по очереди без потерь; запись,
   спорящая с закреплением, выбрасывается; старая по `Used` удаляется; `OpenIfExists` без файла
   возвращает null.
4. Вставка из буфера: с `labels.dat` метки берутся и записываются в хранилище; без файла результат
   совпадает со старым.
5. `Anonymizer`: `[HOST_1]` в данных становится `[~HOST_1]`; голое значение после первой выдачи
   скрыто; переводы строк сохранены; ошибка базы не даёт текста.
6. `LeakGuard`: длинное значение внутри слова скрыто; base64 и hex дают `[ENCODED:...]`;
   `f s 0 1 . c o r p . e x a m p l e` даёт `[SPLIT:...]`; обычный текст с метками не трогается.
7. Подстановка: голое слово берётся в кавычки; удвоение кавычек, включая U+2019; экранирование
   `$`, обратного апострофа и `"`; значение `x'; Remove-Item C:\ -Recurse; '` остаётся одним
   аргументом (в конвейере одна команда, аргумент равен значению); метка в переменной, типе
   и комментарии отклоняется; неизвестная и секретная метки отклоняются; `[~HOST_1]` даёт текст.
8. Исполнитель: выражение, переменная, блок `{ }` и `a; b` отклоняются; `Remove-Item` не найден;
   `Get-Date` работает; `Env:` и `HKLM:` отклоняются правилом аргументов; путь вне корней
   и `Set-Location` вне корней отклоняются, внутри работают; `ipconfig /release` отклоняется,
   `ipconfig /all` проходит проверку (проверять функцию правил, без запуска); задача
   `ping.exe -n 3 127.0.0.1` при `wait_sec` 1 даёт номер, `sp_next` дожидается конца;
   `stop` останавливает; частичный вывод отдаётся полными строками.
9. Файлы на временных данных: нумерация и страницы `sp_read`; двоичный и слишком большой файл
   отклоняются; файл в cp1251 читается; `sp_find` с меткой в шаблоне находит строку; `sp_list`.
10. Протокол на потоках в памяти: `initialize` с 2025-06-18 отвечает 2025-06-18, с неизвестной
    версией 2025-06-18; `tools/list` отдаёт семь инструментов со схемами; неизвестный инструмент
    даёт ошибку; битый JSON даёт -32700; уведомление без ответа; `ping` отвечает `{}`.
11. Хуки: сообщение с IP блокируется, в причине нет самого IP; `!raw ...` проходит; чистое
    проходит; `mcp__safepaste__sp_run` разрешён; `Bash`, `shell`, `apply_patch` получают отказ.

## Готово, когда

- `build.cmd check` проходит: оба exe собраны, `test` и `uitest` без провалов, новые проверки есть.
- `pwsh tools\BridgeSmoke.ps1` проходит.
- `install.ps1` на временных папках создаёт рабочую приватную папку для Codex и для Claude.
- Скилл, MCP-BRIDGE.md, ARCHITECTURE.md и README.md описывают то, что сделано.
- Отчёт пользователю: что сделано, что проверено, что осталось проверить вживую в Codex
  (пункты «Не проверено» выше) и что требует его согласия: запуск `install.ps1` на настоящих
  папках, доверие к приватной папке в Codex и пробная сессия Codex, которая отправит
  обезличенные данные в OpenAI.

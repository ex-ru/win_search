# Руководство по интеграции DigUp с ИИ-агентами (Model Context Protocol / MCP)

Модуль **DigUp MCP** предоставляет ИИ-агентам и большим языковым моделям (Claude, GPT-4, Cursor, Antigravity, OpenCode и др.) прямой доступ к проиндексированной локальной базе знаний и RAG **строго в режиме Read-Only**.

---

## 🔒 Архитектура безопасности и Read-Only изоляция

1. **Режим SQLite WAL (Write-Ahead Logging):**
   * Базы данных (`index.sqlite` и `code.sqlite`) открываются исключительно на чтение (`ReadOnly`).
   * Агенты могут выполнять параллельные запросы к базе одновременно с работающим фоновым индексатором DigUp без взаимных блокировок.
2. **Нулевая возможность мутации:**
   * Сервер MCP не содержит ни одного инструмента или метода для модификации, удаления или создания файлов.
   * Любые DDL/DML запросы (`INSERT`, `UPDATE`, `DELETE`, `DROP`) физически запрещены на уровне подключения к БД.
3. **Защита системных файлов:**
   * Инструмент `read_document_content` оснащен встроенными проверками: блокирует доступ к чувствительным реестрам и системным файлам Windows (`SAM`, `SECURITY`, `SYSTEM`).
   * Чтение выполняется по безопасному смещению (`start_line`, `max_lines`) с защитой от переполнения контекстного окна модели.

---

## 🛠️ Набор доступных инструментов (MCP Tools)

Сервер автоматически объявляет клиенту следующий набор инструментов:

| Инструмент | Параметры | Описание |
|---|---|---|
| `search_documents` | `query` (строка, обяз.)<br>`limit` (число, 1..50, по умолч. 5)<br>`kind` (опц.: `pdf`, `img`, `doc`, `code`, `screenshot`) | Гибридный семантический (векторный) и полнотекстовый (FTS5) поиск по личным файлам. Префикс `code:` переключает на поиск по коду. |
| `read_document_content` | `path` (строка, обяз.)<br>`start_line` (число, по умолч. 1)<br>`max_lines` (число, по умолч. 200, макс. 2000) | Безопасное чтение текстового содержимого или сегментов PDF по указанному пути. |
| `get_document_info` | `path` (строка, обяз.) | Получение метаданных файла из индекса (размер, дата изменения, статус, количество сегментов). |
| `get_index_status` | *нет* | Сводная статистика локального индекса (количество сегментов, векторов, статус модели). |

---

## 🚀 Способ 1. Нативное подключение через `digup mcp` (Рекомендуется)

Не требует установки Python, Node.js или дополнительных библиотек. Работает «из коробки» сразу после установки DigUp.

### 1. Claude Desktop
Откройте конфигурационный файл Claude Desktop:
`%APPDATA%\Claude\claude_desktop_config.json`

Добавьте секцию `mcpServers`:
```json
{
  "mcpServers": {
    "digup-rag": {
      "command": "C:\\Users\\<Ваш_Пользователь>\\AppData\\Local\\DigUp\\current\\digup.exe",
      "args": ["mcp"]
    }
  }
}
```

### 2. Cursor IDE
В корне вашего проекта создайте файл `.cursor/mcp.json` (или укажите в настройках **Features → MCP Servers**):
```json
{
  "mcpServers": {
    "digup": {
      "command": "C:\\Users\\<Ваш_Пользователь>\\AppData\\Local\\DigUp\\current\\digup.exe",
      "args": ["mcp"]
    }
  }
}
```

### 3. Google Antigravity IDE / VS Code / Cline
В файле `mcp_config.json`:
```json
{
  "mcpServers": {
    "local-rag": {
      "command": "C:\\Users\\<Ваш_Пользователь>\\AppData\\Local\\DigUp\\current\\digup.exe",
      "args": ["mcp"]
    }
  }
}
```

### 4. Docker Agent (`agent.yaml`)
```yaml
agents:
  my_assistant:
    model: gpt-4o
    toolsets:
      - type: mcp
        command: "C:\\Users\\<Ваш_Пользователь>\\AppData\\Local\\DigUp\\current\\digup.exe"
        args: ["mcp"]
```

---

## 🐍 Способ 2. Python FastMCP коннектор (`tools/mcp/digup_mcp.py`)

Если ваш проект или фреймворк агентов использует Python (LangChain, AutoGen, CrewAI, LlamaIndex):

### Запуск через `uv`:
```bash
uv run tools/mcp/digup_mcp.py
```

### Использование как Python-библиотеки:
```python
from tools.mcp.digup_mcp import DigUpClient

client = DigUpClient()

# Поиск по документам
results = client.search("финансовый отчет квартал 2025", limit=5)
for hit in results:
    print(f"[{hit['score']:.2f}] {hit['path']}")
    print(f"Цитата: {hit.get('excerpt')}\n")
```

---

## 🧪 Проверка работоспособности через терминал

Вы можете протестировать ответ MCP сервера напрямую через PowerShell, передав тестовый JSON-RPC запрос:

### Проверка рукопожатия (Initialize):
```powershell
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}' | digup mcp
```
**Ожидаемый ответ:**
```json
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"digup-mcp","version":"1.0.4"}}}
```

### Проверка поиска:
```powershell
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_documents","arguments":{"query":"счет 88392","limit":2}}}' | digup mcp
```

---

## 💡 Рекомендации по системному промпту (System Prompt)

Для максимальной точности добавьте в системную инструкцию вашего агента:

```markdown
У тебя есть доступ к локальной базе знаний через инструменты `digup`:
1. `search_documents`: используй при любых вопросах о личных документах, проектах, реквизитах, счетах, переписке и коде.
2. `read_document_content`: вызывай для детального чтения файла, если выжимки из поиска недостаточно.
3. Всегда цитируй точные пути к файлам и страницы/строки в ответе пользователю.
4. Все операции выполняются строго в режиме чтения (Read-Only).
```

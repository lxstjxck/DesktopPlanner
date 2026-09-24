# DesktopPlanner — локальный календарь

Нативные виджеты Windows на .NET 10, WPF, MVVM и SQLite. Реализованы задачи, заметки, недельный календарь, входящие события, управление окнами, миграции базы и синхронизация одного календаря iCloud через CalDAV.

[![Скачать установщик для Windows x64](https://img.shields.io/badge/Скачать_установщик-Windows_x64-4A90D9?style=for-the-badge&logo=github&logoColor=white)](https://github.com/lxstjxck/EnhancedToDo/releases/download/v0.1.0/DesktopPlanner-Setup-0.1.0-win-x64.exe)

## Структура

```text
DesktopPlanner.sln
src/
  DesktopPlanner.Domain/
    Entities.cs, WidgetLayout.cs
  DesktopPlanner.Application/
    Contracts.cs, PlannerService.cs
    CalendarService.cs          # календарные операции, snapping, геометрия недели
  DesktopPlanner.Infrastructure/
    SqlitePlannerStore.cs        # транзакции и EF mapping
    DatabaseMigrator.cs          # принятие legacy-схемы, backup, миграции
    PlannerDbContextFactory.cs
    Migrations/                 # baseline, индекс, snapshot
    WindowsOverlayService.cs    # Win32, DPI, hotkey, click-through
  DesktopPlanner.App/
    App.xaml / .cs              # DI, lifecycle, скрытое окно сообщений
    PlannerViewModel.cs, CalendarViewModel.cs
    WidgetWindow.xaml / .cs
    WidgetContent.xaml
    TodoView.xaml / .cs
    InboxView.xaml / .cs
    WeekView.xaml / .cs          # WPF rendering и жесты
    TrayController.cs          # управление через трей
    GlassBackdropService.cs    # материал из обоев
    app.manifest
 tests/
  DesktopPlanner.Domain.Tests/
  DesktopPlanner.Application.Tests/
  DesktopPlanner.Infrastructure.Tests/
 scripts/Smoke.ps1
 docs/VALIDATION.md
 docs/SPEC.txt
```

Domain не зависит от WPF/EF/Win32. Application зависит только от Domain. ViewModel вызывает Application-сервисы; WPF-геометрия и жесты находятся в Views, P/Invoke — в Infrastructure. SQLite IO выполняется вне UI-потока; операции сериализуются. CalendarViewModel последовательно выполняет команды и ожидает их завершения при выходе.

## Синхронизация iCloud

В трее выберите **«Подключение iCloud…»**, введите Apple Account и отдельный пароль приложения, нажмите **«Найти календари»**, выберите календарь с правом записи и нажмите **«Подключить и синхронизировать»**. [Инструкция Apple по созданию пароля приложения](https://support.apple.com/ru-ru/102654).

В выбранный календарь отправляются все ещё не привязанные к iCloud события, включая запланированные задачи. Заметки, незапланированные задачи и входящие события остаются локальными. Синхронизация запускается при старте приложения, каждые две минуты и вручную из трея. Без сети можно продолжать работу; изменения сохраняются в SQLite.

Обычные события можно создавать, переносить, редактировать и удалять с обеих сторон. Повторы разворачиваются для просматриваемой недели с учётом исключений; повторы и приглашения доступны только для просмотра. Цвета остаются локальными; цвет повторяющегося события применяется ко всей серии.

Полученные из iCloud события также создают связанные задачи в списке слева. Уже загруженные события добавятся при ближайшей успешной синхронизации. Повторный обмен не создаёт дубликаты и не сбрасывает выполнение; перенос события обновляет дату задачи, переименование обновляет название, если задача не была переименована отдельно. Для серии повторов создаётся одна задача. Удаление календарного события сохраняет задачу без расписания; удалённая вручную задача повторно не создаётся при последующих обновлениях.

Пароль хранится только в Windows Credential Manager. При отключении он удаляется, обмен прекращается, локальные события остаются. Повторное подключение того же календаря продолжает обмен. Ранее привязанные к другому календарю события автоматически в новый календарь не копируются.

При конфликте изменений сохраняется версия iCloud и отдельная локальная копия с пометкой в названии. Если удалённое локально событие успели изменить в iCloud, удаление отменяется и возвращается версия iCloud. Статус виден в трее и окне подключения. История Ctrl+Z очищается при применении обмена, меняющего записи; отмена уже отправленных действий пока не реализована.

Подробности реализации, ограничения и ручная проверка: [docs/SYNC.md](docs/SYNC.md).

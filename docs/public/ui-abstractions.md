# YFex.UI.Abstractions & YFex.Mvvm

**Libraries:** `YFex.UI.Abstractions`, `YFex.Mvvm`

YFex.UI.Abstractions defines platform-agnostic contracts for dialogs, message boxes, toasts, and notifications -- the UI interactions that every application needs but that differ wildly across WPF, Avalonia, MAUI, and Blazor. Every result-producing interaction returns a discriminated union (`OkCancel`, `YesNo`, `YesNoCancel`) that forces exhaustive handling at the call site. No null results, no integer codes, no stringly-typed button names. YFex.Mvvm provides the ViewModel base classes that inject these services and bridge the State engine with NavigatR's lifecycle.

---

## Core Concepts & Mental Model

### UI Service Contracts

Four interfaces define all UI interactions. Each is injected via DI and used from ViewModels:

| Interface | Purpose | Result Model |
|---|---|---|
| `IDialog` | Modal/modeless custom dialogs backed by a View type parameter | Generic `TResult` or void |
| `IMessageBox` | Simple question/message popups | `YesNo`, `YesNoCancel`, `OkCancel`, or custom `DialogOption<T>` |
| `IToast` | Ephemeral status messages | None (fire-and-forget) |
| `INotification` | Rich notifications with optional timeout | `YesNo`/`OkCancel` unions, with `Ignored` variants for timeouts |

### Union Result Types

All question-style interactions return discriminated unions. The `Ignored` variants appear only when a timeout is configured -- if the user does not respond in time, the result is `Ignored` rather than null or an exception:

| Type | Cases | When Used |
|---|---|---|
| `OkCancel` | `Ok`, `Cancel` | Proceed/cancel confirmations |
| `YesNo` | `Yes`, `No` | Binary questions |
| `YesNoCancel` | `Yes`, `No`, `Cancel` | Three-way decisions (save/discard/cancel) |
| `OkCancelIgnored` | `Ok`, `Cancel`, `Ignored` | `OkCancel` + timeout |
| `YesNoIgnored` | `Yes`, `No`, `Ignored` | `YesNo` + timeout |
| `YesNoCancelIgnored` | `Yes`, `No`, `Cancel`, `Ignored` | `YesNoCancel` + timeout |

These are defined as `readonly union` types with sealed class cases:

```csharp
namespace YFex.UI.Abstractions;

public readonly union OkCancel(OkCancel.Ok, OkCancel.Cancel)
{
    public sealed class Ok;
    public sealed class Cancel;
}
```

Extension methods provide boolean shorthands: `.IsConfirmed()`, `.IsProceeded()`, `.IsCancelled()`, `.IsIgnored()`.

### ViewModel Hierarchy

The ViewModel stack builds from bottom to top, each layer adding capabilities:

```
StateObject                    Reactive state engine ([Observable], [Computed], [StateCommand])
  MvvmStateObject              INotifyPropertyChanged / INotifyDataErrorInfo bridge
    ViewModel                  + INotification, IDialog, IToast (DI-injected)
      PageViewModel            + Navigator, INavigable lifecycle
        PageViewModel<TResult> + Returns(T), Cancel(), Deny() for typed results
```

Specialized non-navigable ViewModels also inherit from `ViewModel`:

| Class | Purpose | Navigable? |
|---|---|---|
| `PageViewModel` | Full-page screens with navigation lifecycle | Yes |
| `PageViewModel<TResult>` | Pages that return a typed result | Yes |
| `EditorViewModel<TModel>` | Form editing with model lifecycle | Yes (inherits `PageViewModel`) |
| `MasterDetailViewModel` | Master/detail layouts | No -- embed in a `PageViewModel` |
| `ListViewModel` | Paginated/collection lists | No -- embed in a `PageViewModel` |
| `SelectorViewModel` | Pickers/dropdowns | No -- embed in a `PageViewModel` |

---

## Integration Model & Lifecycle

### DI Registration

Platform implementations of `IDialog`, `IMessageBox`, `IToast`, and `INotification` are registered in DI. The `ViewModel` constructor receives them:

```csharp
using YFex.UI.Abstractions;

// Platform-specific registration (example for Avalonia)
services.AddSingleton<IDialog, AvaloniaDialogService>();
services.AddSingleton<IMessageBox, AvaloniaMessageBoxService>();
services.AddSingleton<IToast, AvaloniaToastService>();
services.AddSingleton<INotification, AvaloniaNotificationService>();
```

### ViewModel Constructor

`ViewModel` exposes the three services as properties. `PageViewModel` adds the `Navigator`:

```csharp
using YFex.Mvvm;
using YFex.NavigatR;
using YFex.UI.Abstractions;

public abstract class ViewModel : MvvmStateObject
{
    public INotification Notification { get; }
    public IDialog       Dialog       { get; }
    public IToast        Toast        { get; }
}

public abstract class PageViewModel : ViewModel, INavigable
{
    public Navigator Navigator { get; }
}
```

Both classes provide parameterless constructors for test subclasses that do not use DI. Service properties will be null in that case.

### PageViewModel Lifecycle Integration

The `PageViewModel` lifecycle is driven by the `Navigator`:

```
DI creates instance (constructor injection)
  OnNavigation(context, ct)          Route parameters arrive
    Activate()                       Wires [Subscribe<T>], creates [Live] states
      OnActivateCascading()          Generator hook
        Page displayed
          OnSuspend(ct)              Pushed to back-stack
            OnSuspendCascading()     Pauses [Live] fetching
            Deactivate()
          OnResume(ct)               Returned from back-stack
            OnResumeCascading()      Restarts [Live], re-fetches stale data
            Activate()
        Dispose()                    Page destroyed
          Deactivate()               Unsubscribes all, disposes [Live] states
```

> **Warning:** If you override `OnSuspend` or `OnResume`, always call `base.OnSuspend(ct)` / `base.OnResume(ct)`. The base implementations call `OnSuspendCascading()` / `OnResumeCascading()`, which generated `[Live]` code relies on to pause and restart data fetching.

---

## Step-by-Step Usage

### Show a Dialog

```csharp
using YFex.UI.Abstractions;

// In a ViewModel:

// Fire-and-forget -- returns a handle
IDialogHandle handle = Dialog.Show<AboutView>();

// Await dismissal
await Dialog.ShowAsync<AboutView>();

// Dialog with result
bool confirmed = await Dialog.ShowAsync<ConfirmDeleteView, bool>();

// Fire-and-forget with handle, await result later
IDialogHandle<bool> handle = Dialog.Show<ConfirmDeleteView, bool>();
// ... do other work ...
bool confirmed = await handle.UntilClosedAsync();

// Close programmatically
handle.Close();
```

The `TView` type parameter identifies the view/control to display. The platform implementation resolves it to the actual UI element.

### Ask a Question

```csharp
using YFex.UI.Abstractions;

// Yes/No question
YesNo answer = MessageBox.Ask("Confirm", "Delete this item?").YesNo();
if (answer.IsConfirmed())
{
    await DeleteItemAsync();
}

// Three-option question
YesNoCancel answer = MessageBox.Ask("Save?", "Save changes before closing?").YesNoCancel();
if (answer.IsConfirmed())
    await SaveAsync();
else if (answer.IsCancelled())
    return; // Don't close

// Ok/Cancel
OkCancel answer = MessageBox.Ask("Proceed?", "This action cannot be undone.").OkCancel();
if (answer.IsProceeded())
    await ExecuteAsync();

// Custom choices
string format = MessageBox.Ask("Format", "Choose export format")
    .WithChoices(new DialogOption<string>[]
    {
        new("PDF", "pdf", IsDefault: true),
        new("CSV", "csv"),
        new("Excel", "xlsx"),
    });

// Simple messages (no result)
MessageBox.ShowInfo("Success", "Order saved.");
MessageBox.ShowError("Error", "Connection failed.");
MessageBox.ShowWarning("Warning", "Disk space low.");
MessageBox.ShowSuccess("Done", "Export complete.");
```

### Display a Toast

```csharp
using YFex.UI.Abstractions;

Toast.ShowSuccess("Saved!");
Toast.ShowError("Network error", duration: TimeSpan.FromSeconds(10));
Toast.ShowWarning("Disk space low");
Toast.ShowInfo("3 new messages");
```

Default durations: `ShowSuccess` / `ShowInfo` = 3 seconds, `ShowWarning` / `ShowError` = 5 seconds. Override by passing `duration`.

### Show a Notification

```csharp
using YFex.UI.Abstractions;

// Fire-and-forget (no result)
Notification.Notify<UpdateBannerView>();

// With result -- no timeout
INotificationHandle<OkCancel> handle = Notification
    .Notify<UpdateAvailableView, OkCancel>()
    .OkCancel();
OkCancel result = await handle.UntilClosedAsync();
if (result.IsProceeded()) { /* install update */ }

// With timeout -- adds Ignored to the union
INotificationHandle<OkCancelIgnored> handle = Notification
    .Notify<UpdateAvailableView, OkCancel>()
    .WithTimeout(TimeSpan.FromSeconds(30))
    .OkCancel();
OkCancelIgnored result = await handle.UntilClosedAsync();
if (result.IsProceeded()) { /* install */ }
else if (result.IsIgnored()) { /* user didn't respond in time */ }

// Dismiss programmatically
handle.Dismiss();
```

> **Note:** When you add `.WithTimeout()`, the return type changes from `OkCancel` to `OkCancelIgnored` (or `YesNo` to `YesNoIgnored`, etc.). This is a compile-time guarantee: you cannot forget to handle the timeout case.

---

## Deep Dive: Core API

### IDialog

```csharp
namespace YFex.UI.Abstractions;

public interface IDialog
{
    // Fire-and-forget, returns handle
    IDialogHandle Show<TView>(IDialogOptions? options = null);
    IDialogHandle<TResult> Show<TView, TResult>(IDialogOptions? options = null);

    // Blocking await
    Task ShowAsync<TView>(IDialogOptions? options = null);
    Task<TResult> ShowAsync<TView, TResult>(IDialogOptions? options = null);
}

public interface IDialogHandle
{
    void Close();
    Task UntilClosedAsync();
}

public interface IDialogHandle<TResult> : IDialogHandle
{
    new Task<TResult> UntilClosedAsync();
}
```

`IDialogOptions` is a marker interface. Define custom options records for your platform:

```csharp
public record MyDialogOptions(double Width, double Height, bool Modal = true) : IDialogOptions;

await Dialog.ShowAsync<SettingsView>(new MyDialogOptions(800, 600));
```

### IMessageBox

```csharp
namespace YFex.UI.Abstractions;

public interface IMessageBox
{
    void ShowMessage(string title, string message, MessageIcon icon = MessageIcon.None);
    IMessageBoxAskBuilder Ask(string title, string question, MessageIcon icon = MessageIcon.Question);
}

public interface IMessageBoxAskBuilder
{
    YesNo YesNo();
    YesNoCancel YesNoCancel();
    OkCancel OkCancel();
    TResult WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);
}
```

Extension methods on `IMessageBox`:

| Method | Icon |
|---|---|
| `ShowInfo(title, message)` | `MessageIcon.Info` |
| `ShowWarning(title, message)` | `MessageIcon.Warning` |
| `ShowError(title, message)` | `MessageIcon.Error` |
| `ShowSuccess(title, message)` | `MessageIcon.Success` |

### IToast

```csharp
namespace YFex.UI.Abstractions;

public interface IToast
{
    void Show(string message, MessageIcon icon, TimeSpan duration);
}
```

Extension methods with default durations:

| Method | Icon | Default Duration |
|---|---|---|
| `ShowSuccess(message, duration?)` | `Success` | 3 seconds |
| `ShowInfo(message, duration?)` | `Info` | 3 seconds |
| `ShowWarning(message, duration?)` | `Warning` | 5 seconds |
| `ShowError(message, duration?)` | `Error` | 5 seconds |

### INotification

```csharp
public interface INotification
{
    void Notify<TView>(INotificationOptions? options = null);
    INotificationBuilder Notify<TView, TResult>(INotificationOptions? options = null);
}

public interface INotificationBuilder
{
    INotificationHandle<YesNo> YesNo();
    INotificationHandle<YesNoCancel> YesNoCancel();
    INotificationHandle<OkCancel> OkCancel();
    INotificationHandle<TResult> WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);

    INotificationBuilderWithTimeout WithTimeout(TimeSpan duration);
}

public interface INotificationBuilderWithTimeout
{
    INotificationHandle<YesNoIgnored> YesNo();
    INotificationHandle<YesNoCancelIgnored> YesNoCancel();
    INotificationHandle<OkCancelIgnored> OkCancel();
    INotificationHandle<TResult> WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);
}
```

The builder chain enforces type safety: calling `.WithTimeout()` shifts the return types to `*Ignored` variants, making the timeout case impossible to miss.

### DialogOption\<TResult\>

```csharp
namespace YFex.UI.Abstractions;

public readonly record struct DialogOption<TResult>(
    string Label,
    TResult Result,
    bool IsDefault = false,
    bool IsCancel = false
);
```

### MessageIcon

```csharp
public enum MessageIcon
{
    None,
    Success,
    Info,
    Error,
    Warning,
    Question
}
```

---

## Common Patterns & Recipes

### Confirmation Dialog Before Destructive Action

```csharp
using YFex.UI.Abstractions;

[StateCommand]
public async Task DeleteOrderAsync(CancellationToken ct)
{
    var answer = MessageBox.Ask("Delete Order", "This cannot be undone. Continue?").OkCancel();

    if (!answer.IsProceeded())
        return;

    await _api.DeleteOrderAsync(OrderId, ct);
    Toast.ShowSuccess("Order deleted.");
    await Navigator.NavigateBackward();
}
```

### Save-Before-Close Pattern

```csharp
using YFex.NavigatR;
using YFex.UI.Abstractions;

[Route(typeof(EditorRoute))]
public partial class DocumentEditorViewModel : PageViewModel
{
    public override async Task OnNavigation(NavigationContext context, CancellationToken ct)
    {
        // Guard: ask to save when navigating away with unsaved changes
        if (context.Direction == NavigationDirection.Backward && HasUnsavedChanges)
        {
            var answer = MessageBox
                .Ask("Unsaved Changes", "Save changes before leaving?")
                .YesNoCancel();

            if (answer.IsConfirmed())
                await SaveAsync(ct);
            else if (answer.IsCancelled())
            {
                context.Deny("User cancelled");
                return;
            }
            // No = discard changes and continue
        }

        await base.OnNavigation(context, ct);
    }
}
```

### Toast After Async Operation

```csharp
[StateCommand]
public async Task ExportReportAsync(CancellationToken ct)
{
    try
    {
        await _exporter.ExportAsync(ReportId, ct);
        Toast.ShowSuccess("Report exported.");
    }
    catch (ExportException ex)
    {
        Toast.ShowError($"Export failed: {ex.Message}");
    }
}
```

### Timed Notification with Fallback

```csharp
using YFex.UI.Abstractions;

public async Task CheckForUpdatesAsync()
{
    var update = await _updateService.CheckAsync();
    if (update is null) return;

    var handle = Notification
        .Notify<UpdateBannerView, OkCancel>()
        .WithTimeout(TimeSpan.FromSeconds(30))
        .OkCancel();

    var result = await handle.UntilClosedAsync();

    if (result.IsProceeded())
        await _updateService.InstallAsync(update);
    else if (result.IsIgnored())
        _logger.LogInfo("User did not respond to update notification.");
    // Cancel = user explicitly dismissed
}
```

### Custom Dialog with Options

```csharp
using YFex.UI.Abstractions;

public async Task ChooseExportFormatAsync()
{
    var format = MessageBox
        .Ask("Export", "Choose format:")
        .WithChoices(new DialogOption<ExportFormat>[]
        {
            new("PDF Document", ExportFormat.Pdf, IsDefault: true),
            new("CSV Spreadsheet", ExportFormat.Csv),
            new("Raw JSON", ExportFormat.Json),
        });

    await _exporter.ExportAsync(format);
}
```

---

## Testing & Mocking

### Testing ViewModels with UI Services

All UI service interfaces are mockable. The `ViewModel` parameterless constructor is available for tests that do not need them:

```csharp
public class OrderViewModelTests
{
    [Fact]
    public async Task Delete_asks_confirmation_then_deletes()
    {
        // Arrange
        var messageBox = Substitute.For<IMessageBox>();
        var askBuilder = Substitute.For<IMessageBoxAskBuilder>();
        messageBox.Ask(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageIcon>())
            .Returns(askBuilder);
        askBuilder.OkCancel().Returns(new OkCancel.Ok());

        var toast = Substitute.For<IToast>();
        var api = Substitute.For<IOrderApi>();

        var vm = new OrderViewModel(api, messageBox, toast);
        vm.OrderId = Guid.NewGuid();

        // Act
        await vm.DeleteOrderAsync(CancellationToken.None);

        // Assert
        await api.Received(1).DeleteOrderAsync(vm.OrderId, Arg.Any<CancellationToken>());
        toast.Received(1).Show("Order deleted.", MessageIcon.Success, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Delete_cancelled_does_not_delete()
    {
        var messageBox = Substitute.For<IMessageBox>();
        var askBuilder = Substitute.For<IMessageBoxAskBuilder>();
        messageBox.Ask(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageIcon>())
            .Returns(askBuilder);
        askBuilder.OkCancel().Returns(new OkCancel.Cancel());

        var api = Substitute.For<IOrderApi>();
        var vm = new OrderViewModel(api, messageBox, Substitute.For<IToast>());

        await vm.DeleteOrderAsync(CancellationToken.None);

        await api.DidNotReceive().DeleteOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
```

### Testing Notification Timeout Handling

```csharp
[Fact]
public async Task Update_check_handles_timeout()
{
    var notification = Substitute.For<INotification>();
    var builder = Substitute.For<INotificationBuilder>();
    var timeoutBuilder = Substitute.For<INotificationBuilderWithTimeout>();
    var handle = Substitute.For<INotificationHandle<OkCancelIgnored>>();

    notification.Notify<UpdateBannerView, OkCancel>(null).Returns(builder);
    builder.WithTimeout(Arg.Any<TimeSpan>()).Returns(timeoutBuilder);
    timeoutBuilder.OkCancel().Returns(handle);
    handle.UntilClosedAsync().Returns(new OkCancelIgnored.Ignored());

    var vm = new SettingsViewModel(notification);
    await vm.CheckForUpdatesAsync();

    // Verify update was NOT installed when user ignored
    // ...
}
```

---

## Troubleshooting & Gotchas

**Union types are not enums**
`OkCancel`, `YesNo`, etc. are `readonly union` types with sealed class cases, not enums. Pattern match with `is` or use `.Switch()`, not `==`:

```csharp
// Correct
if (result is OkCancel.Ok) { ... }
if (result.IsProceeded()) { ... }

// Wrong -- will not compile
if (result == OkCancel.Ok) { ... }
```

**Timeout changes the return type**
Adding `.WithTimeout()` shifts the result type (e.g., `OkCancel` becomes `OkCancelIgnored`). This is by design -- the compiler forces you to handle the `Ignored` case. If you remove a timeout, update the variable type.

**IDialogOptions is a marker interface**
Pass `null` (the default) or create a custom record implementing `IDialogOptions`. There are no built-in options -- the options structure is platform-specific.

**Toast.Show is fire-and-forget**
There is no handle or callback from a toast. If you need to know when it was dismissed or tapped, use `INotification` instead.

**Missing base.OnSuspend / base.OnResume**
`PageViewModel.OnSuspend()` calls `OnSuspendCascading()` and `PageViewModel.OnResume()` calls `OnResumeCascading()`. If you override these methods without calling `base`, any generated `[Live]` state management code will not execute, causing stale data or memory leaks.

**ViewModel parameterless constructor nulls**
The parameterless constructor exists for tests. `Notification`, `Dialog`, `Toast`, and `Navigator` will all be null. Calling them in production code when constructed this way will throw `NullReferenceException`.

---

## Reference Summary

### Union Type Cases

| Type | Cases | Extension Methods |
|---|---|---|
| `OkCancel` | `Ok`, `Cancel` | `.IsProceeded()` |
| `OkCancelIgnored` | `Ok`, `Cancel`, `Ignored` | `.IsProceeded()`, `.IsIgnored()` |
| `YesNo` | `Yes`, `No` | `.IsConfirmed()` |
| `YesNoIgnored` | `Yes`, `No`, `Ignored` | `.IsConfirmed()`, `.IsIgnored()` |
| `YesNoCancel` | `Yes`, `No`, `Cancel` | `.IsConfirmed()`, `.IsCancelled()` |
| `YesNoCancelIgnored` | `Yes`, `No`, `Cancel`, `Ignored` | `.IsConfirmed()`, `.IsCancelled()`, `.IsIgnored()` |

### MessageIcon Values

| Value | Used By |
|---|---|
| `None` | Default for `ShowMessage` |
| `Success` | `ShowSuccess`, `Toast.ShowSuccess` |
| `Info` | `ShowInfo`, `Toast.ShowInfo` |
| `Error` | `ShowError`, `Toast.ShowError` |
| `Warning` | `ShowWarning`, `Toast.ShowWarning` |
| `Question` | Default for `Ask` |

### ViewModel Hierarchy

| Class | Inherits | Adds | Navigable |
|---|---|---|---|
| `MvvmStateObject` | `StateObject` | `INotifyPropertyChanged`, `INotifyDataErrorInfo` | No |
| `ViewModel` | `MvvmStateObject` | `Notification`, `Dialog`, `Toast` | No |
| `PageViewModel` | `ViewModel` | `Navigator`, `INavigable` lifecycle | Yes |
| `PageViewModel<T>` | `PageViewModel` | `Returns(T)`, `Cancel()`, `Deny()` | Yes |
| `EditorViewModel<T>` | `PageViewModel` | Form model lifecycle | Yes |
| `MasterDetailViewModel` | `ViewModel` | Master/detail orchestration | No |
| `ListViewModel` | `ViewModel` | Collection/pagination | No |
| `SelectorViewModel` | `ViewModel` | Pick/dropdown | No |

### INotification Builder Chain

```
Notification.Notify<TView, TResult>()
  INotificationBuilder
    .YesNo()            INotificationHandle<YesNo>
    .YesNoCancel()      INotificationHandle<YesNoCancel>
    .OkCancel()         INotificationHandle<OkCancel>
    .WithChoices(...)   INotificationHandle<TResult>
    .WithTimeout(duration)
      INotificationBuilderWithTimeout
        .YesNo()        INotificationHandle<YesNoIgnored>
        .YesNoCancel()  INotificationHandle<YesNoCancelIgnored>
        .OkCancel()     INotificationHandle<OkCancelIgnored>
        .WithChoices()  INotificationHandle<TResult>
```

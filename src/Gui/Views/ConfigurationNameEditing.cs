using System.Windows;
using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Gui.Views;

internal sealed class ConfigurationNameEditing(Window window, Func<Guid, string, bool, bool> save)
{
    private sealed record Edit(Guid ConfigurationId, WpfTextBox Input);

    private Edit? _edit;
    private bool _committing;
    internal bool IsEditing => _edit is not null;

    internal void Begin(Guid id, WpfTextBox input, string name)
    {
        var edit = new Edit(id, input);
        _edit = edit;
        input.Text = name;
        input.Visibility = Visibility.Visible;
        input.BringIntoView();
        input.PreviewKeyDown += Input_PreviewKeyDown;
        input.LostKeyboardFocus += Input_LostKeyboardFocus;
        window.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), true);
        // A menu can restore focus to its own button while it closes.
        input.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => {
            if (ReferenceEquals(_edit, edit)) {
                input.Focus();
                input.SelectAll();
            }
        });
    }

    internal bool Commit(bool unattended)
    {
        if (_edit is not { } edit || _committing) {
            return true;
        }
        _committing = true;
        try {
            var saved = save(edit.ConfigurationId, edit.Input.Text, unattended);
            Cancel();
            return saved;
        } finally {
            _committing = false;
        }
    }

    internal void Cancel(WpfTextBox? input = null)
    {
        if (_edit is not { } edit || input is not null && input != edit.Input) {
            return;
        }
        _edit = null;
        input = edit.Input;
        input.PreviewKeyDown -= Input_PreviewKeyDown;
        input.LostKeyboardFocus -= Input_LostKeyboardFocus;
        window.RemoveHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown));
        input.Select(0, 0);
        if (input.IsKeyboardFocusWithin) {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(input), null);
            Keyboard.ClearFocus();
        }
        input.Visibility = Visibility.Collapsed;
    }

    private void Input_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter) {
            Commit(unattended: false);
        } else if (e.Key == Key.Escape) {
            Cancel();
        } else {
            return;
        }
        e.Handled = true;
    }

    private void Input_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit(unattended: false);

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_edit is { Input: var input } && !input.IsMouseOver
            && (e.OriginalSource is not DependencyObject source || source != input && !input.IsAncestorOf(source))) {
            e.Handled |= !Commit(unattended: false);
        }
    }
}

using System.Windows;
using System.Windows.Input;

namespace PhotoSweep.Desktop.Behaviors;

/// <summary>
/// Attached behaviour: <c>behaviors:FolderDrop.Command="{Binding DropFoldersCommand}"</c> lets an element accept dropped
/// files and folders and passes their paths to the command. It only translates WPF drag events into a command call;
/// deciding which paths to keep is the view-model's job, so that part is unit-tested.
/// </summary>
public static class FolderDrop
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FolderDrop), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        // Unhook first so re-binding the command never attaches the handlers twice.
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;
        element.AllowDrop = e.NewValue is not null;
        if (e.NewValue is null)
            return;

        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject element || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        var command = GetCommand(element);
        IReadOnlyList<string> parameter = paths;
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
        e.Handled = true;
    }
}

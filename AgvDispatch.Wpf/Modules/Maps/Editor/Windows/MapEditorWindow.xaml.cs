using System.Windows;
using AgvDispatch.Wpf.ViewModels;

namespace AgvDispatch.Wpf;

public partial class MapEditorWindow : Window
{
    private bool _activated;
    public string? InitialMapId { get; set; }

    public MapEditorWindow()
    {
        InitializeComponent();
        Loaded += OnEditorLoaded;
        Closed += OnEditorClosed;
    }

    private async void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (_activated || EditorView.DataContext is not MapManagementViewModel viewModel) return;
        _activated = true;
        await viewModel.ActivateEditorAsync(InitialMapId);
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        if (EditorView.DataContext is MapManagementViewModel viewModel) viewModel.DeactivateEditor();
    }
}

using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Media;
using AgvDispatch.Wpf.ViewModels;

namespace AgvDispatch.Wpf.Views;

public partial class MapManagementView : UserControl
{
    private void OpenPointCloud_OnClick(object sender,RoutedEventArgs e)=>ShowPointCloud(null);
    private void OpenCurrentNativePointCloud_OnClick(object sender,RoutedEventArgs e)
    {
        if(DataContext is MapManagementViewModel vm&&vm.IsSmap3DPlanePreview)ShowPointCloud(vm.BackgroundFilePath);
    }
    private void ShowPointCloud(string? source)
    {
        var viewer=new PointCloudView();var window=new Window{Title="三维点云只读查看 · Robokit / XYZ",Content=viewer,Width=1200,Height=900,MinWidth=900,MinHeight=650,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=Window.GetWindow(this)};
        window.Closed+=(_,_)=>viewer.Deactivate();window.Show();if(source is not null)_=viewer.LoadFileAsync(source);
    }
    private void OpenInspector_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MapManagementViewModel vm && sender is Button button && int.TryParse(button.Tag?.ToString(), out var index))
        { vm.ShowMapSetup = false; vm.ShowInspector = true; InspectorTabs.SelectedIndex = index; }
    }
    private void CloseEditorPanel_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MapManagementViewModel vm) vm.ShowInspector = false;
    }
    private void OpenMapSetup_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MapManagementViewModel vm) vm.ShowMapSetup = true;
    }
    private Point? _selectionStart;
    private Point? _panStart;
    private bool _draggingNodes;
    private bool _draggingBoundaryVertex;
    private bool _draggingCurveControl;
    private bool _panWithLeftButton;

    public MapManagementView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachCadViewport();
        DataContextChanged += (_, _) => { if (IsLoaded) AttachCadViewport(); };
        CanvasViewport.SizeChanged += (_, _) => QueueCadViewportUpdate();
        Unloaded += (_, _) => { ResetPointerInteraction(); DetachCadViewport(); };
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => ResetPointerInteraction()));
    }

    private MapManagementViewModel? _cadViewportModel;
    private bool _cadViewportUpdateQueued;
    private void AttachCadViewport()
    {
        DetachCadViewport();
        _cadViewportModel = DataContext as MapManagementViewModel;
        if (_cadViewportModel is not null) _cadViewportModel.PropertyChanged += CadViewportPropertyChanged;
        QueueCadViewportUpdate();
    }
    private void DetachCadViewport()
    {
        if (_cadViewportModel is not null) _cadViewportModel.PropertyChanged -= CadViewportPropertyChanged;
        _cadViewportModel = null;
    }
    private void CadViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "CanvasZoom" or "CanvasPanX" or "CanvasPanY" or "UnderlayImage") QueueCadViewportUpdate();
    }
    private void QueueCadViewportUpdate()
    {
        if (_cadViewportUpdateQueued || !IsLoaded) return;
        _cadViewportUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _cadViewportUpdateQueued = false;
            if (!IsLoaded || DataContext is not MapManagementViewModel vm || CanvasViewport.ActualWidth <= 0) return;
            var first = CanvasViewport.TranslatePoint(new Point(0, 0), MapCanvas);
            var last = CanvasViewport.TranslatePoint(new Point(CanvasViewport.ActualWidth, CanvasViewport.ActualHeight), MapCanvas);
            var dpi = VisualTreeHelper.GetDpi(this);
            vm.UpdateCadViewport(new Rect(first, last), (int)(CanvasViewport.ActualWidth * dpi.DpiScaleX),
                (int)(CanvasViewport.ActualHeight * dpi.DpiScaleY));
        }));
    }

    private void ResetPointerInteraction()
    {
        _panStart = null; _selectionStart = null;
        _panWithLeftButton = false;
        _draggingNodes = false; _draggingBoundaryVertex = false; _draggingCurveControl = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        if (DataContext is not MapManagementViewModel viewModel) return;
        viewModel.EndNodeDrag(); viewModel.EndBoundaryVertexDrag(); viewModel.EndCurveControlDrag();
    }

    private void MapCanvas_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not Canvas canvas) return;
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && viewModel.IsPanMode))
        {
            _panWithLeftButton = e.ChangedButton == MouseButton.Left;
            _panStart = e.GetPosition(this);
            canvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var point = e.GetPosition(canvas);
        if (viewModel.IsCurveControlMode)
        {
            _draggingCurveControl = viewModel.BeginCurveControlDrag(point.X, point.Y);
            if (_draggingCurveControl) canvas.CaptureMouse();
            else if (viewModel.FindEdgeAtCanvas(point.X, point.Y) is { } curve)
            {
                EdgesGrid.SelectedItems.Clear();
                EdgesGrid.SelectedItems.Add(curve);
                viewModel.SelectedEdge = curve;
                EdgesGrid.ScrollIntoView(curve);
            }
            e.Handled = true;
            return;
        }
        if (viewModel.IsBoundaryVertexMode)
        {
            _draggingBoundaryVertex = viewModel.BeginBoundaryVertexDrag(point.X, point.Y);
            if (_draggingBoundaryVertex) canvas.CaptureMouse();
            e.Handled = _draggingBoundaryVertex;
            return;
        }
        if (!viewModel.IsCanvasEditMode) return;
        var hit = viewModel.FindNodeAtCanvas(point.X, point.Y);
        if (hit is not null)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!NodesGrid.SelectedItems.Contains(hit))
            {
                if (!additive) NodesGrid.SelectedItems.Clear();
                NodesGrid.SelectedItems.Add(hit);
            }
            else if (additive)
            {
                NodesGrid.SelectedItems.Remove(hit);
                e.Handled = true;
                return;
            }
            NodesGrid.ScrollIntoView(hit);
            _draggingNodes = viewModel.BeginNodeDrag(
                NodesGrid.SelectedItems.Cast<EditableMapNode>(),
                point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
        }
        else if (viewModel.FindEdgeAtCanvas(point.X, point.Y) is { } edge)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!EdgesGrid.SelectedItems.Contains(edge))
            {
                if (!additive) EdgesGrid.SelectedItems.Clear();
                EdgesGrid.SelectedItems.Add(edge);
            }
            else if (additive)
            {
                EdgesGrid.SelectedItems.Remove(edge);
            }
            EdgesGrid.ScrollIntoView(edge);
        }
        else
        {
            _selectionStart = point;
            SelectionRectangle.Visibility = Visibility.Visible;
            UpdateSelectionRectangle(point, point);
        }
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void MapCanvas_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not Canvas canvas) return;
        var point = e.GetPosition(canvas);
        viewModel.UpdateWorkbenchPointer(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
        if (_panStart is { } panStart && (e.MiddleButton == MouseButtonState.Pressed || (_panWithLeftButton && e.LeftButton == MouseButtonState.Pressed)))
        {
            var current = e.GetPosition(this);
            var transform = canvas.TransformToAncestor(this);
            var origin = transform.Transform(new Point(0, 0));
            var axis = transform.Transform(new Point(1, 0));
            var viewScale = Math.Max(0.01, Math.Abs(axis.X - origin.X) / Math.Max(viewModel.CanvasZoom, 0.1));
            viewModel.PanCanvas((current.X - panStart.X) / viewScale, (current.Y - panStart.Y) / viewScale);
            _panStart = current;
            e.Handled = true;
            return;
        }
        if (_draggingCurveControl && e.LeftButton == MouseButtonState.Pressed)
        {
            viewModel.DragCurveControl(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
            e.Handled = true;
        }
        else if (_draggingBoundaryVertex && e.LeftButton == MouseButtonState.Pressed)
        {
            viewModel.DragBoundaryVertex(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
            e.Handled = true;
        }
        else if (_draggingNodes && e.LeftButton == MouseButtonState.Pressed)
        {
            viewModel.DragSelectedNodes(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
            e.Handled = true;
        }
        else if (_selectionStart is { } selectionStart && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelectionRectangle(selectionStart, point);
            e.Handled = true;
        }
    }

    private void MapCanvas_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not Canvas canvas) return;
        if (e.ChangedButton == MouseButton.Middle || (_panWithLeftButton && e.ChangedButton == MouseButton.Left))
        {
            _panStart = null;
            _panWithLeftButton = false;
            canvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var point = e.GetPosition(canvas);
        if (viewModel.IsCurveControlMode || _draggingCurveControl)
        {
            viewModel.EndCurveControlDrag();
            _draggingCurveControl = false;
            canvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_draggingBoundaryVertex)
        {
            viewModel.EndBoundaryVertexDrag();
            _draggingBoundaryVertex = false;
            canvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (viewModel.IsCanvasEditMode)
        {
            if (_draggingNodes)
            {
                viewModel.EndNodeDrag();
            }
            else if (_selectionStart is { } start)
            {
                var selected = viewModel.FindNodesInCanvasRectangle(start.X, start.Y, point.X, point.Y);
                var selectedEdges = viewModel.FindEdgesInCanvasRectangle(start.X, start.Y, point.X, point.Y);
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    NodesGrid.SelectedItems.Clear();
                    EdgesGrid.SelectedItems.Clear();
                }
                foreach (var node in selected)
                    if (!NodesGrid.SelectedItems.Contains(node)) NodesGrid.SelectedItems.Add(node);
                foreach (var edge in selectedEdges)
                    if (!EdgesGrid.SelectedItems.Contains(edge)) EdgesGrid.SelectedItems.Add(edge);
            }
            _draggingNodes = false;
            _selectionStart = null;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            canvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        viewModel.HandleMapClick(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
        viewModel.UpdateWorkbenchPointer(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
    }

    private void MapCanvas_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not Canvas canvas) return;
        var anchor = e.GetPosition(canvas);
        viewModel.AdjustCanvasZoom(e.Delta, anchor.X, anchor.Y, canvas.ActualWidth, canvas.ActualHeight);
        e.Handled = true;
    }

    private void UpdateSelectionRectangle(Point start, Point current)
    {
        Canvas.SetLeft(SelectionRectangle, Math.Min(start.X, current.X));
        Canvas.SetTop(SelectionRectangle, Math.Min(start.Y, current.Y));
        SelectionRectangle.Width = Math.Abs(current.X - start.X);
        SelectionRectangle.Height = Math.Abs(current.Y - start.Y);
    }

    private void MapGrid_OnBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (DataContext is MapManagementViewModel viewModel) viewModel.BeginGridEdit();
    }

    private void MapGrid_OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        var cancelled = e.EditAction == DataGridEditAction.Cancel;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (DataContext is MapManagementViewModel viewModel) viewModel.CompleteGridEdit(cancelled);
            },
            DispatcherPriority.Background);
    }

    private void NodesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not DataGrid grid) return;
        viewModel.UpdateBatchNodeSelection(grid.SelectedItems.Cast<EditableMapNode>());
    }

    private void EdgesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MapManagementViewModel viewModel || sender is not DataGrid grid) return;
        viewModel.UpdateBatchEdgeSelection(grid.SelectedItems.Cast<EditableMapEdge>());
    }

    public void CancelCurrentInteraction()
    {
        ResetPointerInteraction();
        MapCanvas.ReleaseMouseCapture();
        if (DataContext is MapManagementViewModel vm) vm.CancelWorkbenchTool();
    }
    private void NewMap_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapManagementViewModel vm || !vm.CanOpenNewMap) return;
        var dialog = new AgvDispatch.Wpf.NewMapWindow(vm) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) { vm.ShowMapSetup = true; vm.CancelWorkbenchTool(); }
    }
    private void BrowseCad_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapManagementViewModel vm || !vm.CanEditWorkbench) return;
        var picker = AgvDispatch.Wpf.NewMapWindow.CadPicker();
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.SelectCadFile(picker.FileName);
        }
    }
    private void MapCanvas_OnRightButtonDown(object sender, MouseButtonEventArgs e)
    { CancelCurrentInteraction(); e.Handled = true; }
    private void Workbench_OnPreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { CancelCurrentInteraction(); e.Handled = true; } }
    private void OpenDetails_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapManagementViewModel vm || sender is not Button button) return;
        vm.ShowDetails = true;
        foreach (var item in DetailsTabs.Items.OfType<TabItem>())
        {
            var title = item.Header is TextBlock text ? text.Text : item.Header?.ToString();
            if (title == button.Tag?.ToString()) { DetailsTabs.SelectedItem = item; return; }
        }
    }
    private void Inspector_OnBeginEdit(object sender, KeyboardFocusChangedEventArgs e)
    { if (DataContext is MapManagementViewModel vm && vm.CanEditWorkbench) vm.BeginGridEdit(); }
    private void Inspector_OnEndEdit(object sender, KeyboardFocusChangedEventArgs e)
    { if (DataContext is MapManagementViewModel vm && vm.CanEditWorkbench) vm.CompleteGridEdit(false); }
}

using System.Windows;
using System.Windows.Controls;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.ViewModels;
using Microsoft.Win32;

namespace AgvDispatch.Wpf;

public partial class NewMapWindow : Window
{
    private readonly MapManagementViewModel _viewModel;
    public NewMapWindow(MapManagementViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => NameBox.Focus();
    }
    internal static OpenFileDialog CadPicker() => new()
    { Title = "选择此地图的CAD文件", Filter = "CAD文件 (*.dwg;*.dxf)|*.dwg;*.dxf", CheckFileExists = true, Multiselect = false };
    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = CadPicker();
        if (picker.ShowDialog(this) == true) BackgroundBox.Text = picker.FileName;
    }
    private async void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { ErrorText.Text = "请填写地图名称。"; NameBox.Focus(); return; }
        var unit = ((ComboBoxItem)UnitBox.SelectedItem).Tag.ToString()!;
        var scale = unit == "mm" ? 0.001 : unit == "cm" ? 0.01 : 1;
        CreateButton.IsEnabled = false;
        try
        {
            if (await _viewModel.CreateProjectAsync(new CreateMapRequest(NameBox.Text.Trim(), DescriptionBox.Text.Trim(),
                BackgroundBox.Text.Trim(), "LocalCartesian", unit, scale))) DialogResult = true;
            else ErrorText.Text = _viewModel.StatusMessage;
        }
        finally { CreateButton.IsEnabled = true; }
    }
}

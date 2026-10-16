using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;
using Hc = HandyControl.Controls;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.ViewModels;

internal static class Program
{
    private static int _checks;
    [STAThread] private static void Main()
    {
        var app = new System.Windows.Application();
        foreach (var uri in new[] { "HandyControl;component/Themes/SkinDefault.xaml", "HandyControl;component/Themes/Theme.xaml",
            "AgvDispatch.Wpf;component/Themes/Light.xaml", "AgvDispatch.Wpf;component/Themes/HandyPalette.xaml", "AgvDispatch.Wpf;component/Styles/Typography.xaml",
            "AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml", "AgvDispatch.Wpf;component/Styles/Controls.xaml",
            "AgvDispatch.Wpf;component/Styles/HandyControls.xaml" })
            app.Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/" + uri) });
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => null!);
        var theme = new ThemeService();
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var fields = new StackPanel { Margin = new Thickness(0,0,24,0) };
        fields.Children.Add(new TextBlock { Text = "通信连接设置", FontSize = 25, FontWeight = FontWeights.SemiBold });
        fields.Children.Add(new TextBlock { Text = "保留蓝色工业风格，统一控件与主题", Margin = new Thickness(0,8,0,24) });
        fields.Children.Add(new TextBlock { Text = "PLC地址", Margin = new Thickness(0,0,0,6) });
        var box = new TextBox { Text = "192.168.1.10" }; fields.Children.Add(box);
        var empty = new TextBox { Margin = new Thickness(0,12,0,0) };
        Hc.InfoElement.SetPlaceholder(empty, "输入车辆IP或名称"); fields.Children.Add(empty);
        var combo = new ComboBox { ItemsSource = new[] { "S1200", "S1500", "S300" }, SelectedIndex = 0, Margin = new Thickness(0,12,0,0) }; fields.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,20,0,0) };
        var primary = new Button { Content = "保存配置", Style = (Style)app.FindResource("AppPrimaryButtonStyle") };
        var secondary = new Button { Content = "重新加载", Margin = new Thickness(12,0,0,0) };
        buttons.Children.Add(primary); buttons.Children.Add(secondary); fields.Children.Add(buttons);
        fields.Children.Add(new CheckBox { Content = "启用只读采集", IsChecked = true, Margin = new Thickness(0,20,0,0) });
        var notifications = new StackPanel(); Grid.SetColumn(notifications,1);
        Hc.Growl.SetToken(notifications, HandyUserFeedback.Token);
        root.Children.Add(fields); root.Children.Add(notifications);
        var window = new Window { Width = 1080, Height = 650, Left = -10000, Top = -10000, ShowInTaskbar = false, Content = root };
        window.SetResourceReference(Window.BackgroundProperty,"AppBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty,"TextPrimaryBrush");
        window.Show();
        for (var mode = 0; mode < 2; mode++)
        {
            if (mode == 1) theme.Toggle();
            var name = mode == 0 ? "light" : "dark";
            Pump();
            Check(Contrast(box.Foreground,box.Background) >= 4.5, name + " input contrast");
            Check(Contrast(combo.Foreground,combo.Background) >= 4.5, name + " dropdown contrast");
            Check(Contrast(primary.Foreground,primary.Background) >= 4.5, name + " primary button contrast");
            Check(Contrast(secondary.Foreground,secondary.Background) >= 4.5, name + " secondary button contrast");
            foreach (var token in new[] { "TextSecondaryBrush", "SuccessBrush", "WarningBrush", "DangerBrush", "InfoBrush", "OfflineBrush" })
                Check(Contrast((Brush)app.FindResource(token),(Brush)app.FindResource("SurfaceBrush")) >= 4.5, name + " " + token + " contrast");
            Check(box.Template is not null && combo.Template is not null, name + " HC templates");
            Console.WriteLine($"{name}: HC text {((SolidColorBrush)app.FindResource("PrimaryTextBrush")).Color}, app text {((SolidColorBrush)app.FindResource("TextPrimaryBrush")).Color}, HC color {app.FindResource("PrimaryTextColor")}");
            Check(((SolidColorBrush)app.FindResource("PrimaryTextBrush")).Color == ((SolidColorBrush)app.FindResource("TextPrimaryBrush")).Color, name + " HC text synchronized");
            Check(((SolidColorBrush)app.FindResource("RegionBrush")).Color == ((SolidColorBrush)app.FindResource("SurfaceBrush")).Color, name + " HC popup surfaces synchronized");
            combo.IsDropDownOpen = true; Pump();
            Check(combo.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem, name + " popup generated");
            combo.IsDropDownOpen = false;
            var feedback = new HandyUserFeedback(); feedback.Success("连接配置已保存，安全停机后重启后台生效。");
            feedback.Warning("此为控件测试，未连接PLC或AGV。"); Pump();
            Check(notifications.Children.Count > 0, name + " scoped Growl");
            Render(root,"theme-controls-" + name);
            Hc.Growl.Clear(HandyUserFeedback.Token);
            // Render real affected pages without navigation, service calls or model mutations.
            using var client = new NamedPipeTrafficControlClient("ThemeTest.NeverConnect");
            var vm = new ConnectionSettingsViewModel(client);
            var configuration = new CommunicationConfiguration { Plcs = [new() { DeviceId="PLC-01",DisplayName="总控PLC",IpAddress="192.168.1.10",SignalCsvPath="config/plc-signals.csv" }], Agvs = [new() { VehicleId="AGV-01",DisplayName="运输一号",IpAddress="192.168.1.20" }] };
            typeof(ConnectionSettingsViewModel).GetMethod("Apply",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm,[new CommunicationSettingsSnapshot("test",configuration,configuration,false,true,[],[])]);
            var settings = new ConnectionSettingsView { DataContext = vm };
            Render(settings,"settings-theme-" + name);
            var statusLabel = Find<TextBlock>(settings).First(t => System.Windows.Data.BindingOperations.GetBinding(t,TextBlock.TextProperty)?.Path?.Path == "ApplyStatus");
            Check(Contrast(statusLabel.Foreground,(Brush)app.FindResource("MutedSurfaceBrush")) >= 4.5, name + " real settings status label");
            Render(new MapManagementView(),"map-editor-theme-" + name);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var dialogChecked = false;
            timer.Tick += (_, _) =>
            {
                foreach (var dialog in app.Windows.OfType<Hc.MessageBox>().ToArray())
                {
                    dialogChecked = true; Render(dialog,"dialog-theme-" + name, true);
                    dialog.Close();
                }
                timer.Stop();
            };
            timer.Start();
            var result = Hc.MessageBox.Show(window,"保存仅更新后台连接配置，不自动启用车辆动作。","保存连接配置",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel);
            Check(dialogChecked && result != MessageBoxResult.OK, name + " themed HC dialog defaults to no write");
        }
        theme.Toggle(); Check(!theme.IsDark, "roundtrip theme switch");
        window.Close(); Console.WriteLine($"PASS: {_checks} theme/control checks, no device commands.");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle);
    private static void Render(FrameworkElement element,string name, bool preserveSize = false)
    {
        if (element is Grid grid) grid.SetResourceReference(Grid.BackgroundProperty,"AppBackgroundBrush");
        else if (element is UserControl control) control.SetResourceReference(Control.BackgroundProperty,"AppBackgroundBrush");
        var width = preserveSize ? Math.Max(320,element.ActualWidth) : 1032;
        var height = preserveSize ? Math.Max(180,element.ActualHeight) : 590;
        if (!preserveSize) { element.Width=width; element.Height=height; element.Measure(new(width,height)); element.Arrange(new(0,0,width,height)); }
        element.UpdateLayout(); Pump();
        var frame = new DispatcherFrame(); var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        settle.Tick += (_,_) => {settle.Stop();frame.Continue=false;};settle.Start();Dispatcher.PushFrame(frame);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width),(int)Math.Ceiling(height),96,96,PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(),name + ".png");
        using(var stream = File.Create(path)) encoder.Save(stream); Console.WriteLine(path);
    }
    private static double Contrast(Brush a,Brush b)
    {
        IEnumerable<Color> Colors(Brush brush) => brush is SolidColorBrush solid ? [solid.Color] : ((GradientBrush)brush).GradientStops.Select(s => s.Color);
        double L(Color c) { double V(byte n){var v=n/255.0;return v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);} return .2126*V(c.R)+.7152*V(c.G)+.0722*V(c.B); }
        return Colors(a).SelectMany(x => Colors(b).Select(y => (Math.Max(L(x),L(y))+.05)/(Math.Min(L(x),L(y))+.05))).Min();
    }
    private static void Check(bool value,string name){ if(!value)throw new InvalidOperationException("FAIL "+name);_checks++;Console.WriteLine("PASS "+name); }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T typed)yield return typed;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}

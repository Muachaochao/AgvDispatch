using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new AgvDispatch.Wpf.App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var vm = new TaskCenterViewModel(new NamedPipeTrafficControlClient("AgvDispatch.Service"));
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        var view = new TaskCenterView { DataContext = vm };
        vm.OnNavigatedTo(null!);
        PumpUntil(() => !vm.IsBusy && vm.Detail is not null);
        Require(vm.Tasks.Count > 0 && vm.Steps.Count == 9, "Named Pipe list, detail and nine-step rendering");
        var chosen = vm.Tasks.FirstOrDefault(t => t.VehicleId == "AGV-01");
        Require(chosen is not null, "isolated fixture required; do not use production database");
        vm.SelectedTask = chosen;
        PumpUntil(() => vm.Detail?.Task.TaskId == chosen!.TaskId);
        Require(vm.Commands.Count == 1 && vm.Resources.Count == 1,
            $"commands and traffic resources through service: commands={vm.Commands.Count}, resources={vm.Resources.Count}, task={vm.Detail?.Task.TaskId}, status={vm.StatusMessage}");
        var output = Path.Combine(Path.GetTempPath(), "agv-task-center-ui-" + Guid.NewGuid().ToString("N") + ".png");
        view.Width = 1280; view.Height = 760;
        view.Background = (Brush)app.FindResource("AppBackgroundBrush");
        view.Measure(new Size(1280, 760)); view.Arrange(new Rect(0, 0, 1280, 760)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        var image = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(output)) encoder.Save(stream);
        vm.OnNavigatedFrom(null!);
        Console.WriteLine("PASS: XAML load, view model navigation, Named Pipe list/detail, selection switch and layout render");
        Console.WriteLine(output);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Require(done(), "Timed out waiting for isolated service");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { Console.Error.WriteLine("FAIL: " + message); Environment.Exit(1); }
    }
}

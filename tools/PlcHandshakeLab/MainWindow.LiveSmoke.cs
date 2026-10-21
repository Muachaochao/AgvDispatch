using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PlcHandshakeLab;

public partial class MainWindow
{
    private async Task RunLiveSmokeAsync(string[] args, int position)
    {
        if (position + 1 >= args.Length) { System.Windows.Application.Current.Shutdown(1); return; }
        var path = Path.GetFullPath(args[position + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (_live is null || OfflineControls.IsEnabled || Timeline.IsEnabled) throw new InvalidOperationException("联调模式未隔离离线演示。");
            var originalStage = _session.Current;
            var stationArg = Array.IndexOf(args, "--station");
            if (stationArg >= 0 && stationArg + 1 < args.Length)
                LiveStation.SelectedIndex = args[stationArg + 1] == "CNC-002" ? 1 : 0;
            if (args.Contains("--trigger-call"))
            {
                if (_live.Read(SelectedStation).Values[0]) throw new InvalidOperationException("需要未呼叫工位验证输入，不借用旧高电平。");
                Call_Click(this, new RoutedEventArgs());
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!_live.Read(SelectedStation).Values[0] && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
                if (!_live.Read(SelectedStation).Values[0]) throw new InvalidOperationException("设备未反馈本次呼叫。");
            }
            PollLive();
            SelectStage(9); Play_Click(this, new());
            if (_session.Current != originalStage || _timer.IsEnabled || _viewModel.Signals.Skip(1).Any(s => s.CanToggle))
                throw new InvalidOperationException("联调模式允许离线推进或修改H输出。");
            var state = _live.Read(SelectedStation);
            if (!_viewModel.Signals.Select(s => s.Bit).SequenceEqual(state.Values.Select(v => v ? "1" : "0")))
                throw new InvalidOperationException("实时卡片与设备不一致。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96,96,PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            File.WriteAllText(path + ".result.json",JsonSerializer.Serialize(new { passed = true, station = SelectedStation, snapshot = state, events = _liveEvents },new JsonSerializerOptions { WriteIndented = true }));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".result.json",JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}

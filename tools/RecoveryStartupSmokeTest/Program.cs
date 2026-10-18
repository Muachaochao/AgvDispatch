using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
var checks=0;void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
var recovery=DispatchProxy.Create<IExecutionRecoveryService,RecoveryProxy>();var proxy=(RecoveryProxy)(object)recovery;
using(var lifetime=new Lifetime())using(var worker=new ExecutionRecoveryWorker(recovery,NullLogger<ExecutionRecoveryWorker>.Instance,lifetime)){
 await worker.StartAsync(default);await Task.Delay(100);
 Check(proxy.Initialized==1&&proxy.Inspected==0,"先同步建立恢复冻结，Host未就绪不启动巡检");
 Check(await proxy.Control.WaitAsync(TimeSpan.FromSeconds(1)),"启动阶段后续地图初始化能取得控制门，不与巡检争用");proxy.Control.Release();
 lifetime.Started.Cancel();await proxy.FirstInspection.Task.WaitAsync(TimeSpan.FromSeconds(2));
 Check(proxy.Inspected>0,"ApplicationStarted后才开始周期巡检");
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));await worker.StopAsync(timeout.Token);Check(worker.ExecuteTask?.IsCompleted==true,"就绪后停止可取消周期等待且不遗留巡检线程");
}
recovery=DispatchProxy.Create<IExecutionRecoveryService,RecoveryProxy>();proxy=(RecoveryProxy)(object)recovery;
using(var lifetime=new Lifetime())using(var worker=new ExecutionRecoveryWorker(recovery,NullLogger<ExecutionRecoveryWorker>.Instance,lifetime)){
 await worker.StartAsync(default);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));await worker.StopAsync(timeout.Token);lifetime.Started.Cancel();await Task.Delay(50);Check(proxy.Initialized==1&&proxy.Inspected==0&&worker.ExecuteTask?.IsCompleted==true,"Host未就绪时也能停止，后续就绪信号不复活旧线程");
}
recovery=DispatchProxy.Create<IExecutionRecoveryService,RecoveryProxy>();proxy=(RecoveryProxy)(object)recovery;proxy.FailInitialize=true;
using(var lifetime=new Lifetime())using(var worker=new ExecutionRecoveryWorker(recovery,NullLogger<ExecutionRecoveryWorker>.Instance,lifetime)){
 var failed=false;try{await worker.StartAsync(default);}catch(InvalidOperationException){failed=true;}lifetime.Started.Cancel();await Task.Delay(50);Check(failed&&proxy.Inspected==0,"冻结初始化失败仍阻断启动，不被延迟巡检掩盖");
}
Console.WriteLine($"PASS {checks} recovery startup checks; no database or devices.");
public class RecoveryProxy:DispatchProxy{
 public int Initialized,Inspected;public bool FailInitialize;public SemaphoreSlim Control=new(1,1);public TaskCompletionSource FirstInspection=new(TaskCreationOptions.RunContinuationsAsynchronously);
 protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch{
  "InitializeAsync"=>Initialize(),"InspectAsync"=>Inspect((CancellationToken)args![0]!),_=>throw new NotSupportedException(method?.Name)};
 Task Initialize(){Interlocked.Increment(ref Initialized);return FailInitialize?Task.FromException(new InvalidOperationException("fixture initialization rejected")):Task.CompletedTask;}
 async Task Inspect(CancellationToken ct){Interlocked.Increment(ref Inspected);await Control.WaitAsync(ct);try{FirstInspection.TrySetResult();}finally{Control.Release();}}
}
sealed class Lifetime:IHostApplicationLifetime,IDisposable{
 public CancellationTokenSource Started=new(),Stopping=new(),Stopped=new();public CancellationToken ApplicationStarted=>Started.Token;public CancellationToken ApplicationStopping=>Stopping.Token;public CancellationToken ApplicationStopped=>Stopped.Token;public void StopApplication()=>Stopping.Cancel();public void Dispose(){Started.Dispose();Stopping.Dispose();Stopped.Dispose();}
}

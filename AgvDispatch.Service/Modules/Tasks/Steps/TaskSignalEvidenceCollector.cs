using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>Observes only caches, including during transportation, faults and recovery holds. Never writes devices.</summary>
public sealed class TaskSignalEvidenceCollector(IActiveTaskLookup active,IDispatchTaskRepository tasks,IPlcSignalCache cache,
    ITaskHandshakeStore store,DispatchTaskCreationOptions options,ILogger<TaskSignalEvidenceCollector> logger):IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                foreach(var task in await active.GetActiveTasksAsync(ct))
                {
                    var step=(await tasks.GetStepsAsync(task.TaskId,ct)).FirstOrDefault(s=>s.Status!=DispatchTaskStepStatus.Completed);
                    await store.RecordSignalsAsync(task.TaskId,step?.StepId??"",TaskHandshakeSignals.Capture(cache,task.StationId,options.DumperStationId),ct);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){logger.LogWarning(e,"任务信号变化采集失败，不能据此推断现场无变化。");}
            await Task.Delay(1000,ct);
        }
    }
}

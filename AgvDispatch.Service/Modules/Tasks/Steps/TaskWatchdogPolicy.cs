using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed record TaskWatchdogPolicy(int TimeoutSeconds, string Action, string Source);

public static class TaskWatchdogPolicyResolver
{
    public static TaskWatchdogPolicy ForStep(
        DispatchTaskStepWorkItem step,
        DispatchTaskStepOptions options,
        TaskParameterRuntime? parameters) =>
        ResolveStep(step.StepType, step.TemplateNodeId, step.TimeoutSeconds, step.TimeoutAction, options, parameters);

    public static TaskWatchdogPolicy ForSelection(
        DispatchTaskWorkItem task,
        DispatchTaskStepOptions options)
    {
        if (!string.IsNullOrWhiteSpace(task.TemplateNodeId))
            return new(task.TimeoutSeconds, TaskTemplateExecutionPolicy.NormalizeTimeoutAction(task.TimeoutAction), "Template");
        return new(Math.Clamp(options.SelectAgvTimeoutSeconds, 0, 604800),
            TaskTemplateExecutionPolicy.NormalizeTimeoutAction(options.DefaultTimeoutAction), "Default");
    }

    public static TaskWatchdogPolicy ResolveStep(
        DispatchTaskStepType stepType,
        string? templateNodeId,
        int templateTimeoutSeconds,
        string? templateAction,
        DispatchTaskStepOptions options,
        TaskParameterRuntime? parameters)
    {
        if (!string.IsNullOrWhiteSpace(templateNodeId))
            return new(templateTimeoutSeconds,
                TaskTemplateExecutionPolicy.NormalizeTimeoutAction(templateAction), "Template");
        if (parameters?.HasRuntimeConfiguration == true)
            return new(parameters.GetStepTimeoutSeconds(stepType),
                TaskTemplateExecutionPolicy.NormalizeTimeoutAction(options.DefaultTimeoutAction), "Parameters");
        return new(options.GetDefaultStepTimeoutSeconds(stepType),
            TaskTemplateExecutionPolicy.NormalizeTimeoutAction(options.DefaultTimeoutAction), "Default");
    }
}

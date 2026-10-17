using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.ViewModels;
internal static partial class Program
{
    static void CheckRouteActions(TaskCenterViewModel vm,RoutePreviewClient client,ManualTaskRoutePreview preview,AgvDispatch.Wpf.App app,string evidence)
    {
        Require(preview.Steps.Count==9,"backend attaches same-observation nine-step metadata");
        void Select(int sequence,int part){vm.SelectedCreationRouteSegment=vm.CreationRouteSegments.Single(s=>s.Sequence==sequence&&s.Segment==part);}
        Select(1,1);Require(vm.CreationRouteActionDetails.Contains("JackLoad")&&vm.CreationRouteActionDetails.Contains("BoxPresent=false")&&vm.CreationRouteActionDetails.Contains("PLC CNC001"),"pickup road links load action and next pickup PLC handshake");
        Select(3,1);Require(vm.CreationRouteActionDetails.Contains("DumperReady=true")&&vm.CreationRouteActionDetails.Contains("PLC DUMPER-001")&&!vm.CreationRouteActionDetails.Contains("JackLoad"),"waiting transport shows dumper PLC conditions without old pickup action");
        Select(5,1);Require(vm.CreationRouteActionDetails.Contains("DumpingCompletedMayLeave=true")&&vm.CreationRouteActionDetails.Contains("AgvAtDumpingPosition"),"dump transport links permission-to-leave handshake");
        Select(7,1);Require(vm.CreationRouteActionDetails.Contains("途经子段")&&vm.CreationRouteActionDetails.Contains("JackUnload")&&vm.CreationRouteActionDetails.Contains("第9步"),"return middle subsegment does not execute unload and includes final reset");
        Select(7,2);Require(vm.CreationRouteActionDetails.Contains("当前为末子段")&&vm.CreationRouteActionDetails.Contains("EmptyBoxPlaced=true")&&vm.CreationRouteActionDetails.Contains("非实时信号"),"return terminal links place-box PLC and explicitly marks requirements not live evidence");
        RenderCurrentRouteMap(vm,app,evidence,"return-action-plc",1000,620,false);
        vm.SelectedCreationRouteSegment=null;Require(!vm.CreationRouteActionDetails.Contains("JackUnload"),"deselect clears action and PLC metadata");
        void Load(ManualTaskRoutePreview value){client.Override=_=>Task.FromResult(value with{ObservedAt=DateTimeOffset.Now});Complete(vm.PreviewCreationRoutesAsync());Select(1,1);}
        Load(preview with{Steps=[]});Require(vm.CreationRouteActionDetails.Contains("未提供完整")&&!vm.CreationRouteActionDetails.Contains("JackLoad"),"old backend cannot borrow options metadata for current map");
        Load(preview with{Steps=preview.Steps.Select(s=>s.Sequence==1?s with{DestinationSite="OTHER"}:s).ToArray()});Require(vm.CreationRouteActionDetails.Contains("端点不一致"),"inconsistent transport endpoint cannot display associated action");
        Load(preview with{Steps=preview.Steps.Take(8).Append(preview.Steps[0]).ToArray()});Require(vm.CreationRouteActionDetails.Contains("未提供完整"),"duplicate step metadata rejected");
        Load(preview with{Steps=preview.Steps.Select(s=>s.Sequence==1?null!:s).ToArray()});Require(vm.CreationRouteActionDetails.Contains("未提供完整"),"null deserialized step safely refuses association");
        Load(preview with{Steps=preview.Steps.Select(s=>s.Sequence==1?s with{DestinationSite="LM-PICK"}:s).ToArray()});Require(vm.CreationRouteActionDetails.Contains("业务末站 PICK / 厂家末站 LM-PICK"),"vendor-configured endpoint still displays distinct business and vendor identifiers");
        client.Override=null;Complete(vm.PreviewCreationRoutesAsync());Select(1,1);vm.ClearCreationRoutesCommand.Execute();Require(!vm.CreationRouteActionDetails.Contains("JackLoad"),"clear observation removes associated actions");
        Complete(vm.PreviewCreationRoutesAsync());
    }
}

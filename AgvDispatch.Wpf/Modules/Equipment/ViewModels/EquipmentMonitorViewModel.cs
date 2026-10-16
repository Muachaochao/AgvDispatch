using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Wpf.ViewModels;
public sealed class EquipmentMonitorViewModel(IDeviceMonitoringClient client) : DeviceMonitorViewModel(client, false);

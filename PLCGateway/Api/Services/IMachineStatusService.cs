using PlcApi.Models;

namespace PlcApi.Services;

public interface IMachineStatusService
{
    Task<MachineStatusDto?> GetStatusAsync();
}

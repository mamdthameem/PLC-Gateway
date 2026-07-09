using PlcApi.Models;

namespace PlcApi.Services;

public interface ISpareStatusService
{
    Task<List<SpareStatusDto>> GetAllAsync();
    Task<List<SpareStatusDto>> GetAlertsAsync();
}

using PlcApi.Models;

namespace PlcApi.Services;

public interface ICyclesService
{
    Task<LatestCycleDto?> GetLatestAsync();
}

using PlcApi.Models;

namespace PlcApi.Services;

public interface IFilterService
{
    Task<int> SubmitRequestAsync(FilterRequestInput input);
    Task<FilterStatusDto?> GetStatusAsync(int requestId);
    Task<List<FilterResultDto>> GetResultsAsync(int requestId);
    Task<List<FilteredCycleDto>> GetCycleDataAsync(int requestId);
    Task<List<FilteredMetalProductionDto>> GetMetalProductionAsync(int requestId);
    Task<List<FilteredAmpsDto>> GetAmpsDataAsync(int requestId);
    Task<FilteredRequestInfoDto?> GetLatestCompletedAsync();
}
